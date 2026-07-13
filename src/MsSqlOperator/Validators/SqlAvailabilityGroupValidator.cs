using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroup))]
public class SqlAvailabilityGroupValidator : ValidationWebhook<SqlAvailabilityGroup>
{
    public override ValidationResult Create(SqlAvailabilityGroup entity, bool dryRun) => Validate(entity);

    public override ValidationResult Update(SqlAvailabilityGroup oldEntity, SqlAvailabilityGroup entity, bool dryRun)
    {
        // Real Update — edition is immutable, version downgrades rejected, endpoint
        // ports immutable (recreating the mirroring endpoint interrupts replication).
        if (!string.Equals(oldEntity.Spec.Edition, entity.Spec.Edition, StringComparison.OrdinalIgnoreCase))
            return Fail("spec.edition is immutable. Delete and recreate the AG.", 422);

        var oldVer = (oldEntity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec()).Version;
        var newVer = (entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec()).Version;
        if (oldVer != newVer && !SqlServerImages.IsValidUpgrade(oldVer, newVer))
            return Fail($"Version downgrade from {oldVer} to {newVer} is not supported.", 422);

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

        return Validate(entity);
    }

    private ValidationResult Validate(SqlAvailabilityGroup entity)
    {
        var spec = entity.Spec;
        var isStandard = spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);
        var isEnterprise = spec.Edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
                        || spec.Edition.Equals("Developer", StringComparison.OrdinalIgnoreCase);

        if (!isStandard && !isEnterprise)
            return Fail($"Edition must be Standard, Enterprise, or Developer. Got: {spec.Edition}", 422);

        // ── Image combination — the edition gate above already rejects Express;
        // Standard hosts Basic AGs, so IsAgCompatible (Developer/Enterprise only) is not used.
        var tmpl = spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (!SqlServerImages.IsValidCombination(tmpl.Version, spec.Edition))
            return Fail($"Invalid version/edition combination: {tmpl.Version}/{spec.Edition}", 422);

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

        var endpoint = spec.Endpoint ?? new AgEndpointSpec();
        if (endpoint.Port is < 1024 or > 65535)
            return Fail("endpoint.port must be between 1024 and 65535", 422);

        if (spec.Listener != null && spec.Listener.Port is < 1024 or > 65535)
            return Fail("listener.port must be between 1024 and 65535", 422);

        if (spec.Listener != null && spec.Listener.Port == endpoint.Port)
            return Fail("listener.port must differ from endpoint.port", 422);

        var failover = spec.Failover ?? new AgFailoverSpec();
        if (failover.HealthCheckTimeoutSeconds < 5)
            return Fail("failover.healthCheckTimeoutSeconds must be >= 5", 422);

        if (failover.FailoverConditionLevel is < 1 or > 5)
            return Fail("failover.failoverConditionLevel must be between 1 and 5", 422);

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
                if (ov.AvailabilityMode != null) SqlQuoting.ValidateAvailabilityMode(MapMode(ov.AvailabilityMode));
            }
        }

        if (spec.ReplicaDefaults is { } defaults)
        {
            SqlQuoting.ValidateAvailabilityMode(MapMode(defaults.AvailabilityMode));
            SqlQuoting.ValidateSeedingMode(defaults.SeedingMode.ToUpperInvariant());
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

        // ── Standard-specific rules ───────────────────────────
        if (isStandard)
        {
            if (spec.Replicas.Count != 2)
                return Fail("Standard Edition requires exactly 2 replicas", 422);

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
        var warnings = new List<string>();
        if (TopologySpreadValidator.Validate(
                spec.TopologySpread, spec.AntiAffinity, spec.Pool,
                isSinglePod: spec.Replicas.Count <= 1, warnings) is { } topoErr)
            return Fail(topoErr, 422);

        return Success(warnings.ToArray());
    }

    // The spec exposes camel-cased availability modes; SqlQuoting validates the SQL form.
    private static string MapMode(string mode) => mode switch
    {
        "SynchronousCommit" => "SYNCHRONOUS_COMMIT",
        "AsynchronousCommit" => "ASYNCHRONOUS_COMMIT",
        _ => mode.ToUpperInvariant(),
    };
}
