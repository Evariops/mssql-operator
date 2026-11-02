using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Control;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroup))]
public class SqlAvailabilityGroupValidator(
    MsSqlOperator.Operator.Client.IKubernetesClient? kubernetesClient = null,
    MsSqlOperator.Operator.Registration.OperatorOptions? options = null)
    : ValidationWebhook<SqlAvailabilityGroup>
{
    public override ValidationResult Create(SqlAvailabilityGroup entity, bool dryRun) => Validate(entity);

    public override async Task<ValidationResult> CreateAsync(SqlAvailabilityGroup entity, bool dryRun, CancellationToken ct)
    {
        var result = Validate(entity);
        if (!result.Valid) return result;

        // A SqlInstance of the same name (or one named after a Service this AG
        // derives, e.g. '<name>-headless') shares this AG's children.
        if (kubernetesClient is not null
            && await CrossKindNameGuard.ConflictAsync(kubernetesClient, "SqlAvailabilityGroup",
                entity.Metadata.Name, entity.Metadata.NamespaceProperty, ct) is { } conflict)
            return Fail(conflict, 422);

        return result;
    }

    public override ValidationResult Update(SqlAvailabilityGroup oldEntity, SqlAvailabilityGroup entity, bool dryRun)
    {
        // Real Update — edition is immutable, version downgrades rejected, endpoint
        // ports immutable (recreating the mirroring endpoint interrupts replication).
        if (!string.Equals(oldEntity.Spec.Edition, entity.Spec.Edition, StringComparison.OrdinalIgnoreCase))
            return Fail("spec.edition is immutable. Delete and recreate the AG.", 422);

        // The engine build every replica runs may only move forward — and for an AG the stakes are
        // higher than for a standalone instance: an upgraded replica cannot rejoin an older primary,
        // so a CU that goes backwards mid-rolling-update wedges the group.
        if (BuildDowngrade(oldEntity, entity) is { } downgradeErr)
            return Fail(downgradeErr, 422);

        var oldEndpoint = oldEntity.Spec.Endpoint ?? new AgEndpointSpec();
        var newEndpoint = entity.Spec.Endpoint ?? new AgEndpointSpec();
        if (oldEndpoint.Port != newEndpoint.Port || !string.Equals(oldEndpoint.Name, newEndpoint.Name, StringComparison.Ordinal))
            return Fail("endpoint.name/port are immutable: recreating the mirroring endpoint interrupts replication.", 422);

        // replicas.count is immutable until scale-out/in is orchestrated.
        // A mutable count is a silent corruptor: raising it spawns a pod on a blank PV with no HADR
        // crypto/endpoint (ADD REPLICA + JOIN are never issued), and lowering it leaves a phantom
        // replica in the AG (REMOVE REPLICA is never issued). Fail loudly rather than corrupt.
        if (oldEntity.Spec.Replicas.Count != entity.Spec.Replicas.Count)
            return Fail(
                $"spec.replicas.count is immutable ({oldEntity.Spec.Replicas.Count} → {entity.Spec.Replicas.Count}): " +
                "AG scale-out/in is not orchestrated yet. Recreate the AG to change the replica count.", 422);

        // Nothing checked the AG's volumes — an added or removed volume, a new storage class or
        // a shrink only surfaced as a StatefulSet apply failing on every pass.
        if (StorageMutationValidator.Validate(oldEntity.Spec.InstanceTemplate?.Storage, entity.Spec.InstanceTemplate?.Storage,
                "instanceTemplate.storage") is { } storageErr)
            return Fail(storageErr, 422);

        var result = Validate(entity, oldEntity);
        if (result.Valid
            && !string.Equals(oldEntity.Spec.HadrCertificate?.Generate?.ExpiryDate, entity.Spec.HadrCertificate?.Generate?.ExpiryDate, StringComparison.Ordinal))
            return Success([.. result.Warnings ?? [], "hadrCertificate.generate.expiryDate applies to the certificates created from now on " +
                                                     "(a replica rebuilt on a blank volume); the existing ones keep their expiry."]);
        return result;
    }

    /// <summary>The rejection message when the update moves the replicas' engine build backwards, or
    /// null. Waived by the <c>allow-version-downgrade</c> annotation on the new object.</summary>
    private static string? BuildDowngrade(SqlAvailabilityGroup oldEntity, SqlAvailabilityGroup entity)
    {
        if (SqlServerImages.IsDowngradeApproved(entity.Metadata?.Annotations))
            return null;

        var oldTmpl = oldEntity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        var newTmpl = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (!SqlServerImages.TryResolve(oldTmpl.ImageSelection(oldEntity.Spec.Edition), out var from)
            || !SqlServerImages.TryResolve(newTmpl.ImageSelection(entity.Spec.Edition), out var to))
            return null;

        return SqlServerImages.CheckUpgrade(from, to);
    }

    private ValidationResult Validate(SqlAvailabilityGroup entity, SqlAvailabilityGroup? old = null)
    {
        var spec = entity.Spec;
        var isStandard = spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);
        var isEnterprise = spec.Edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
                        || spec.Edition.Equals("Developer", StringComparison.OrdinalIgnoreCase);

        if (!isStandard && !isEnterprise)
            return Fail($"Edition must be Standard, Enterprise, or Developer. Got: {spec.Edition}", 422);

        // ── Build selection — the edition gate above already
        // rejects Express; Standard hosts Basic AGs, so IsAgCompatible (Developer/Enterprise only)
        // is not used. The image, not spec.version, decides which engine every replica runs.
        var warnings = new List<string>();
        var tmpl = spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (SqlServerImages.CheckSelection(tmpl.ImageSelection(spec.Edition), warnings) is { } selectionErr)
            return Fail($"instanceTemplate: {selectionErr}", 422);

        var previousSelection = old is null ? (SqlImageSelection?)null
            : (old.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec()).ImageSelection(old.Spec.Edition);
        if (SqlServerImages.CheckKnownDefects(tmpl.ImageSelection(spec.Edition), previousSelection, warnings) is { } defectErr)
            return Fail($"instanceTemplate: {defectErr}", 422);

        // ── Memory validation ─────────────────────────────────
        if (tmpl.Memory != null)
        {
            var (valid, message) = MemorySpecValidator.Validate(tmpl.Memory);
            if (!valid) return Fail(message!, 422);
        }

        // ── Common rules ──────────────────────────────────────
        if (spec.SaPasswordSecretRef == null)
            return Fail("saPasswordSecretRef is required", 422);

        if (spec.MasterKeyPasswordSecretRef == null)
            return Fail("masterKeyPasswordSecretRef is required", 422);

        if (spec.HadrCertificate == null)
            return Fail("hadrCertificate is required", 422);

        // Only the Generate/PerReplica flow is implemented by the controller today.
        // Reject the documented-but-unimplemented combinations rather than silently
        // ignoring them (they would otherwise behave as Generate/PerReplica).
        if (!spec.HadrCertificate.Source.Equals("Generate", StringComparison.OrdinalIgnoreCase))
            return Fail($"hadrCertificate.source '{spec.HadrCertificate.Source}' is not supported yet; only 'Generate' is implemented", 422);

        if (!spec.HadrCertificate.Model.Equals("PerReplica", StringComparison.OrdinalIgnoreCase))
            return Fail($"hadrCertificate.model '{spec.HadrCertificate.Model}' is not supported yet; only 'PerReplica' is implemented", 422);

        if (spec.HadrCertificate.Generate?.ExpiryDate is { } expiry
            && SqlValueError(() => SqlQuoting.ValidateDateLiteral(expiry)) is { } expiryError)
            return Fail($"hadrCertificate.generate.expiryDate: {expiryError}", 422);

        // The operator creates the pods — the image and the security contexts must stay within
        // what the author could not otherwise obtain.
        if (options is not null && ImagePolicy.Refusal(tmpl.Image, options.AllowedImageRegistries, "instanceTemplate") is { } imageErr)
            return Fail(imageErr, 422);
        if (SecurityContextPolicy.Check(tmpl.PodSecurityContext, tmpl.SecurityContext, "instanceTemplate") is { } contextErr)
            return Fail(contextErr, 422);
        if (tmpl.Tls is { SecretName: "" })
            return Fail("instanceTemplate.tls.secretName is required: the kubernetes.io/tls Secret every replica presents.", 422);

        // With one synchronous secondary, an AG cannot both lose no acknowledged write and keep
        // writing once a replica is down — the choice must be explicit.
        var synchronousSecondaries = DurabilityProfile.SynchronousSecondaries(spec);
        if (spec.Replicas.Count == 2 && spec.Replicas.Durability is null)
            warnings.Add("spec.replicas.durability is not set on a 2-replica AG: it runs Legacy (writes continue when a replica is down, " +
                         "and an unplanned failover may lose acknowledged writes; condition DurabilityAtRisk). Set durability: Legacy to " +
                         (isStandard
                             ? "accept it: Standard offers no other profile."
                             : "accept it, or durability: Quorum to lose none, at the price of blocking writes while either replica is down."));
        // The default is Legacy there too. Said at creation: the profile is fixed then, and kept.
        else if (old is null && spec.Replicas.Durability is null && synchronousSecondaries < 2)
            warnings.Add("spec.replicas.durability is not set and synchronousCount is below 3: the AG runs Legacy (an unplanned failover " +
                         "may lose acknowledged writes; condition DurabilityAtRisk). Set durability: Legacy to accept it; durability: Quorum " +
                         "to lose none, at the price of blocking writes while the only synchronous secondary is down; or synchronousCount: 3 " +
                         "with durability: Quorum, which keeps writing when one synchronous secondary is down.");
        // An AG that took the Quorum default keeps it, with a single synchronous secondary left. The
        // webhook does not read the control record.
        else if (old is not null && spec.Replicas.Durability is null && spec.Edition == "Enterprise"
                 && DurabilityProfile.SynchronousSecondaries(old.Spec) >= 2 && synchronousSecondaries < 2)
            warnings.Add("synchronousCount goes below 3 while spec.replicas.durability is not set: an AG that took the Quorum default " +
                         "(condition DurabilityAtRisk not True) keeps it, and writes then block as soon as its only synchronous secondary " +
                         "drops. Set durability: Legacy to keep writing, durability: Quorum to confirm it, or keep synchronousCount: 3.");
        // The engine refuses the setting on a basic availability group ("not supported by this edition").
        if (spec.Replicas.Durability == DurabilityProfile.Quorum && isStandard)
            return Fail("spec.replicas.durability: Quorum is not available on Standard: a basic availability group does not support " +
                        "REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT. Use Legacy, or Enterprise.");
        if (spec.Replicas.Durability == DurabilityProfile.Quorum && synchronousSecondaries < 1)
            return Fail("spec.replicas.durability: Quorum needs a synchronous secondary to harden every commit (synchronousCount >= 2).");
        // What the engine does at REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT = 1 with one synchronous
        // secondary: the only copy left after a replica is lost refuses to commit alone. Said when the
        // AG comes to it, not on every apply.
        if (spec.Replicas.Durability == DurabilityProfile.Quorum && synchronousSecondaries == 1
            && !(old?.Spec.Replicas.Durability == DurabilityProfile.Quorum && DurabilityProfile.SynchronousSecondaries(old.Spec) == 1))
            warnings.Add("spec.replicas.durability: Quorum with a single synchronous secondary: no automatic failover loses an acknowledged " +
                         "write, but writes block while that secondary is down (restart, rolling update, node drain), and after a failover " +
                         "until the former primary rejoins (event DurabilityFloorHoldingWrites). " +
                         (spec.Replicas.Count >= 3
                             ? "synchronousCount: 3 keeps writing when one synchronous secondary is down."
                             : "Only a third synchronous replica avoids it, and spec.replicas.count is immutable."));

        if (UnimplementedFieldsError(entity, old, warnings) is { } unimplementedErr)
            return Fail(unimplementedErr, 422);

        var endpoint = spec.Endpoint ?? new AgEndpointSpec();
        if (endpoint.Port is < 1024 or > 65535)
            return Fail("endpoint.port must be between 1024 and 65535", 422);

        // RC4 (alone, or negotiable through "AES RC4") is broken.
        if (SqlValueError(() => SqlQuoting.ValidateEndpointAlgorithm(endpoint.EncryptionAlgorithm)) is not null)
            return Fail("endpoint.encryptionAlgorithm must be AES: RC4 is broken and no longer accepted.", 422);

        if (spec.Listener != null && spec.Listener.Port is < 1024 or > 65535)
            return Fail("listener.port must be between 1024 and 65535", 422);

        if (spec.Listener != null && spec.Listener.Port == endpoint.Port)
            return Fail("listener.port must differ from endpoint.port", 422);

        var failover = spec.Failover ?? new AgFailoverSpec();
        if (failover.HealthCheckTimeoutSeconds < 5)
            return Fail("failover.healthCheckTimeoutSeconds must be >= 5", 422);


        // Databases are now managed by SqlAvailabilityGroupDatabase CRDs — no database validation here

        // Read-scale AGs (synchronousCount == 0) are valid with rsstc == 0; the old
        // check (rsstc >= syncCount → 0 >= 0) rejected them systematically.
        var rsstc = spec.Replicas.RequiredSynchronizedSecondariesToCommit;
        var syncCount = spec.Replicas.SynchronousCount;
        if (!(rsstc == 0 && syncCount == 0) && rsstc > Math.Max(0, syncCount - 1))
            return Fail("requiredSynchronizedSecondariesToCommit must be <= synchronousCount - 1 (or 0 for a read-scale AG with synchronousCount 0)", 422);

        if (spec.Replicas.SynchronousCount > spec.Replicas.Count)
            return Fail("synchronousCount cannot exceed replica count", 422);

        // ── Replica field bounds ──────────────────
        if (spec.Replicas.Overrides is { } overrides)
        {
            var ordinals = new HashSet<int>();
            foreach (var ov in overrides)
            {
                if (ov.Ordinal < 0 || ov.Ordinal >= spec.Replicas.Count)
                    return Fail($"replicas.overrides[].ordinal {ov.Ordinal} is out of range [0, {spec.Replicas.Count})", 422);
                if (!ordinals.Add(ov.Ordinal))
                    return Fail($"replicas.overrides[]: duplicate ordinal {ov.Ordinal}", 422);
                if (ov.BackupPriority is < 0 or > 100)
                    return Fail("replicas.overrides[].backupPriority must be 0-100", 422);
                if (ov.SessionTimeout is < 5)
                    return Fail("replicas.overrides[].sessionTimeout must be >= 5", 422);
                if (ov.AvailabilityMode != null
                    && SqlValueError(() => SqlQuoting.ValidateAvailabilityMode(MapMode(ov.AvailabilityMode))) is { } ovModeError)
                    return Fail($"replicas.overrides[].availabilityMode: {ovModeError}", 422);
            }
        }

        if (spec.ReplicaDefaults is { } defaults)
        {
            // SqlQuoting throws on an invalid value; uncaught, the dispatcher turned a plain
            // input error into an HTTP 500 "internal validation error".
            if (SqlValueError(() => SqlQuoting.ValidateAvailabilityMode(MapMode(defaults.AvailabilityMode))) is { } modeError)
                return Fail($"replicaDefaults.availabilityMode: {modeError}", 422);
            if (SqlValueError(() => SqlQuoting.ValidateSeedingMode(defaults.SeedingMode.ToUpperInvariant())) is { } seedingError)
                return Fail($"replicaDefaults.seedingMode: {seedingError}", 422);
            if (defaults.BackupPriority is < 0 or > 100)
                return Fail("replicaDefaults.backupPriority must be 0-100", 422);
            if (defaults.SessionTimeout is < 5)
                return Fail("replicaDefaults.sessionTimeout must be >= 5", 422);
        }

        if (spec.Failover?.PreferredTargets is { } prefs)
            foreach (var pref in prefs)
                if (pref.Ordinal < 0 || pref.Ordinal >= spec.Replicas.Count)
                    return Fail($"failover.preferredTargets[].ordinal {pref.Ordinal} is out of range [0, {spec.Replicas.Count})", 422);

        if (spec.ReadOnlyListener != null && spec.ReadOnlyListener.Port == endpoint.Port)
            return Fail("readOnlyListener.port must differ from endpoint.port", 422);

        if (spec.BackupPreference != null
            && spec.BackupPreference is not ("Primary" or "SecondaryOnly" or "Secondary" or "None"))
            return Fail("backupPreference must be Primary, SecondaryOnly, Secondary or None", 422);

        if (spec.Failover?.RemoveDisconnectedReplicaAfterMinutes is < 0 or > 1440)
            return Fail($"failover.removeDisconnectedReplicaAfterMinutes must be between 0 (never) and 1440. Got: {spec.Failover.RemoveDisconnectedReplicaAfterMinutes}", 422);

        if (spec.VolumeAutoGrow is { } grow)
        {
            if (grow.Mode is not ("Auto" or "Disabled"))
                return Fail($"volumeAutoGrow.mode must be Auto or Disabled. Got: {grow.Mode}", 422);
            if (grow.ThresholdPercent is < 50 or > 95)
                return Fail($"volumeAutoGrow.thresholdPercent must be between 50 and 95. Got: {grow.ThresholdPercent}", 422);
            if (grow.GrowPercent is < 10 or > 200)
                return Fail($"volumeAutoGrow.growPercent must be between 10 and 200. Got: {grow.GrowPercent}", 422);
            var storage = spec.InstanceTemplate?.Storage;
            foreach (var (field, max, declared) in new[]
                     {
                         ("dataMaxSize", grow.DataMaxSize, (storage?.Data ?? StatefulSetBuilder.DefaultDataVolume).Size),
                         ("logMaxSize", grow.LogMaxSize, (storage?.Log ?? StatefulSetBuilder.DefaultLogVolume).Size),
                         ("backupMaxSize", grow.BackupMaxSize, (storage?.Backup ?? StatefulSetBuilder.DefaultBackupVolume).Size),
                     })
            {
                if (max is null)
                    continue;
                decimal ceiling, size;
                try
                {
                    ceiling = new k8s.Models.ResourceQuantity(max.Trim()).ToDecimal();
                    size = new k8s.Models.ResourceQuantity(declared.Trim()).ToDecimal();
                }
                catch (Exception)
                {
                    return Fail($"volumeAutoGrow.{field} '{max}' is not a valid Kubernetes quantity", 422);
                }
                if (ceiling < size)
                    return Fail($"volumeAutoGrow.{field} ({max}) is below the declared size of the volume ({declared})", 422);
            }
        }

        // ── Standard-specific rules ───────────────────────────
        if (isStandard)
        {
            if (spec.Replicas.Count != 2)
                return Fail("Standard Edition requires exactly 2 replicas", 422);

            // A basic availability group has no REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT and runs
            // at 0, so a floor above it could never be held. requiredSynchronizedSecondariesToCommit
            // itself (0 or 1 with 2 replicas) is ignored.
            if (spec.Replicas.RequiredSyncFloor > 0)
                return Fail("spec.replicas.requiredSyncFloor is not available on Standard: a basic availability group does not support " +
                            "REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT, so the primary always commits without its secondary. Use Enterprise.", 422);

            // Database count per AG is enforced by SqlAvailabilityGroupDatabaseValidator

            if (spec.ReadOnlyListener != null)
                return Fail("ReadOnlyListener is not supported in Standard Edition", 422);

            if (spec.ReadOnlyRouting is { Enabled: true })
                return Fail("Read-Only Routing is not supported in Standard Edition", 422);

            if (spec.BackupPreference != null)
                return Fail("BackupPreference is not supported in Standard Edition", 422);

            // Overrides: no async, no readable secondary
            if (spec.Replicas.Overrides != null)
            {
                foreach (var ov in spec.Replicas.Overrides)
                {
                    if (ov.AvailabilityMode?.Equals("AsynchronousCommit", StringComparison.OrdinalIgnoreCase) == true)
                        return Fail("Standard Edition does not support AsynchronousCommit replicas", 422);

                    if (ov.ReadableSecondary != null && !ov.ReadableSecondary.Equals("No", StringComparison.OrdinalIgnoreCase))
                        return Fail("Standard Edition does not support readable secondaries", 422);

                    if (ov.BackupPriority.HasValue)
                        return Fail("BackupPriority is not supported in Standard Edition", 422);
                }
            }

            // This used to be an empty block — replicaDefaults.readableSecondary was
            // silently accepted for Standard while per-replica overrides were rejected.
            if (spec.ReplicaDefaults?.ReadableSecondary != null
                && !spec.ReplicaDefaults.ReadableSecondary.Equals("No", StringComparison.OrdinalIgnoreCase)
                && !spec.ReplicaDefaults.ReadableSecondary.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Standard Edition does not support readable secondaries (replicaDefaults.readableSecondary must be 'No' or omitted; 'All' is tolerated and overridden to No)", 422);
            }
        }

        // ── Enterprise-specific rules ─────────────────────────
        if (isEnterprise)
        {
            if (spec.Replicas.Count is < 2 or > 9)
                return Fail("Enterprise Edition supports 2-9 replicas", 422);
        }

        // ── Topology spread & pool placement ────────
        if (TopologySpreadValidator.Validate(
                spec.TopologySpread, spec.AntiAffinity, spec.Pool,
                isSinglePod: spec.Replicas.Count <= 1, warnings) is { } topoErr)
            return Fail(topoErr, 422);

        // A SIGKILL mid-checkpoint makes every restart a crash recovery.
        if (ShutdownValidator.Validate(tmpl.Shutdown) is { } shutdownErr)
            return Fail(shutdownErr, 422);

        if (NetworkPolicyValidator.Validate(spec.NetworkPolicy) is { } netpolErr)
            return Fail(netpolErr, 422);

        if (PodAnnotationsValidator.Validate(tmpl.PodAnnotations, "instanceTemplate.podAnnotations") is { } annotationsErr)
            return Fail(annotationsErr, 422);

        // maxDataLossKb only bounds the AUTOMATIC data-loss failover, which must itself
        // be enabled — saying so at admission avoids a false sense of protection.
        if (spec.Failover is { MaxDataLossKb: not null, AllowAutomaticDataLossFailover: false })
            warnings.Add("failover.maxDataLossKb only applies to automatic data-loss failovers, and " +
                         "failover.allowAutomaticDataLossFailover is false — no automatic data-loss failover can happen at all.");

        return Success(warnings.ToArray());
    }

    /// <summary>
    /// Values the schema accepted and the operator ignored, refused at admission (see
    /// <see cref="UnimplementedFields"/> for the ratchet that spares values already stored).
    /// </summary>
    private static string? UnimplementedFieldsError(SqlAvailabilityGroup entity, SqlAvailabilityGroup? old, List<string> warnings)
    {
        var fields = new UnimplementedFields(warnings);
        var hasOld = old is not null;
        var spec = entity.Spec;
        var oldSpec = old?.Spec;

        var rolling = spec.UpdateStrategy?.RollingUpdate;
        var oldRolling = oldSpec?.UpdateStrategy?.RollingUpdate;
        if (rolling is not null)
        {
            fields.Check("updateStrategy.rollingUpdate.maxUnavailable", rolling.MaxUnavailable, oldRolling is not null,
                oldRolling?.MaxUnavailable ?? 0, v => v == 1, "only 1 is implemented: the replicas are updated one at a time.");
            fields.Check("updateStrategy.rollingUpdate.updateSecondariesFirst", rolling.UpdateSecondariesFirst, oldRolling is not null,
                oldRolling?.UpdateSecondariesFirst ?? true, v => v,
                "the rolling update always updates the secondaries (asynchronous, then synchronous) before the primary.");
            fields.Check("updateStrategy.rollingUpdate.preUpdateBackup", rolling.PreUpdateBackup, oldRolling is not null,
                oldRolling?.PreUpdateBackup ?? false, v => !v,
                "the operator takes no backup before restarting a replica; schedule your own and set it to false.");
        }

        var failover = spec.Failover ?? new AgFailoverSpec();
        var oldFailover = oldSpec?.Failover ?? new AgFailoverSpec();
        fields.Check("failover.failoverConditionLevel", failover.FailoverConditionLevel, hasOld, oldFailover.FailoverConditionLevel,
            v => v == AgFailoverSpec.ImplementedFailoverConditionLevel,
            "only level 2 is implemented: the operator fails over when the primary is down or does not answer; " +
            "it does not act on SQL Server errors.");
        fields.Check("failover.autoResumeDataMovement", failover.AutoResumeDataMovement, hasOld, oldFailover.AutoResumeDataMovement,
            v => v, "cannot be disabled: data movement is resumed by each SqlAvailabilityGroupDatabase (suspend a database there).");
        fields.Check("failover.autoReconfigurePrimary", failover.AutoReconfigurePrimary, hasOld, oldFailover.AutoReconfigurePrimary,
            v => v, "cannot be disabled: the role labels follow the primary, and the listener Service selects on them.");
        fields.Check("failover.splitBrainProtection.enabled", failover.SplitBrainProtection?.Enabled ?? true, hasOld,
            oldFailover.SplitBrainProtection?.Enabled ?? true, v => v,
            "fencing the former primary cannot be disabled; fencingStrategy ManualApproval or External leaves it to you " +
            "(the operator then promotes only once the former primary is demoted).");

        fields.Check("operatorLoginSecretRef", spec.OperatorLoginSecretRef is null,
            hasOld && oldSpec!.OperatorLoginSecretRef is not null,
            "is not implemented: the operator connects with the sa login.");

        var hadr = spec.HadrCertificate;
        var oldHadr = oldSpec?.HadrCertificate;
        fields.Check("hadrCertificate.generate.keyLength", hadr?.Generate?.KeyLength ?? 2048, hasOld,
            oldHadr?.Generate?.KeyLength ?? 2048, v => v == 2048,
            "CREATE CERTIFICATE has no key-length option: SQL Server generates a 2048-bit key.");
        fields.Check("hadrCertificate.secretName", hadr?.SecretName is null, hasOld && oldHadr?.SecretName is not null,
            "is not implemented: the certificates are generated on each replica and exchanged by the operator.");
        fields.Check("hadrCertificate.importFrom", hadr?.ImportFrom is null, hasOld && oldHadr?.ImportFrom is not null,
            "is not implemented: only source Generate is.");
        fields.Check("hadrCertificate.encryptionPasswordSecretRef", hadr?.EncryptionPasswordSecretRef is null,
            hasOld && oldHadr?.EncryptionPasswordSecretRef is not null,
            "is not implemented: the operator protects the exported private keys itself.");

        return fields.Error;
    }

    // The spec exposes camel-cased availability modes; SqlQuoting validates the SQL form.
    private static string MapMode(string mode) => mode switch
    {
        "SynchronousCommit" => "SYNCHRONOUS_COMMIT",
        "AsynchronousCommit" => "ASYNCHRONOUS_COMMIT",
        _ => mode.ToUpperInvariant(),
    };

    /// <summary>The message of the <see cref="ArgumentException"/> a SqlQuoting validator throws for
    /// an invalid value, or null when the value is valid.</summary>
    private static string? SqlValueError(Func<string> validate)
    {
        try
        {
            validate();
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }
}
