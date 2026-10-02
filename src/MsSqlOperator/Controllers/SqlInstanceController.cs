using k8s;
using k8s.Models;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlInstance), Verbs = RbacVerb.All)]
public class SqlInstanceController(
    ILogger<SqlInstanceController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder,
    IMssqlConfBuilder mssqlConfBuilder,
    IMachineKeyManager machineKeyManager,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IServiceMasterKeyManager smkManager,
    IFullTextManager fullTextManager,
    ISaPasswordConverger saPasswordConverger,
    IRolloutBudget rolloutBudget,
    MsSqlOperator.Operator.Registration.OperatorOptions options,
    MsSqlOperator.Services.Metrics.OperatorMetrics metrics) : IEntityController<SqlInstance>
{
    public async Task<ReconciliationResult<SqlInstance>> ReconcileAsync(SqlInstance entity, CancellationToken ct)
    {
        try
        {
            return await ReconcileCoreAsync(entity, ct);
        }
        catch (ForeignChildException ex)
        {
            // A child of this name belongs to another custom resource.
            // Stop — loudly. Proceeding would corrupt or destroy the other resource's data.
            logger.LogError(ex, "SqlInstance {Name}: refusing to reconcile — {Message}", entity.Metadata.Name, ex.Message);
            await eventPublisher.PublishAsync(entity, "ForeignChildResource", ex.Message, "Warning", ct);
            entity = await UpdateStatus(entity, entity.Status?.Phase ?? "Pending", "ForeignChildResource", ex.Message, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromMinutes(5));
        }
        catch (StatefulSetNameNotReleasedException ex)
        {
            // The StatefulSet is still terminating after a storage expansion. Requeue
            // instead of applying on top of a dying object (which 422'd and re-triggered the whole
            // patch/delete cycle under backoff).
            logger.LogInformation("SqlInstance {Name}: {Message}", entity.Metadata.Name, ex.Message);
            entity = await UpdateStatus(entity, "Provisioning", "StorageExpansionInProgress", ex.Message, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(5));
        }
    }

    private async Task<ReconciliationResult<SqlInstance>> ReconcileCoreAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlInstance {Name} in {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var previousPhase = entity.Status?.Phase;
        metrics.TrackInstance(ns, name, entity.Spec.Mode, entity.Spec.Version);

        // Resolve SA password (still needed for operator SQL connections)
        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for {Name}", name);
            entity = await UpdateStatus(entity, "Failed", "SaPasswordNotFound", "SA password secret not found", ct);
            // Failed states retry explicitly instead of waiting for the periodic resync.
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(30));
        }

        // The image must come from an allowed registry (defence in depth behind admission).
        if (!ImagePolicy.IsAllowed(entity.Spec.Image, options.AllowedImageRegistries))
        {
            var refused = $"spec.image \"{entity.Spec.Image}\" is not in an allowed registry ({string.Join(", ", options.AllowedImageRegistries)})";
            await eventPublisher.PublishAsync(entity, "ImageRegistryNotAllowed", refused, "Warning", ct);
            entity = await UpdateStatus(entity, "Failed", "ImageRegistryNotAllowed", refused, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromMinutes(5));
        }

        // The configuration that provides mssql.conf — configurationRef, or the single
        // SqlConfiguration selecting this instance by labels.
        entity.Status ??= new SqlInstanceStatus();
        var (sqlConfig, ambiguity) = await SqlConfigurationResolver.ResolveAsync(
            kubernetesClient, ns, entity.Spec.ConfigurationRef, entity.Metadata.Labels, ct);
        ConditionOps.Set(entity.Status.Conditions ??= [], "StartupConfigurationResolved", ambiguity is null ? "True" : "False",
            ambiguity is null ? "Resolved" : "Ambiguous",
            ambiguity ?? (sqlConfig is null ? "No SqlConfiguration applies" : $"mssql.conf from SqlConfiguration {sqlConfig.Metadata.Name}"));
        if (ambiguity is not null)
            await eventPublisher.PublishAsync(entity, "StartupConfigurationAmbiguous", ambiguity, "Warning", ct);

        // mssql.conf. The ConfigMap is applied only once the StatefulSet was accepted.
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, entity.Spec.Memory, entity.Spec.Storage, entity.Spec.RuntimeClassName);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);

        // Ensure machine key secret
        var ownerRef = new V1OwnerReference
        {
            ApiVersion = entity.ApiVersion,
            Kind = entity.Kind,
            Name = name,
            Uid = entity.Metadata.Uid,
            Controller = true,
            BlockOwnerDeletion = true
        };
        string machineKeySecretName;
        try
        {
            machineKeySecretName = await machineKeyManager.EnsureMachineKeySecretAsync(
                name, ns, ownerRef,
                entity.Spec.MachineKeySecretRef?.Name, entity.Spec.MachineKeySecretRef?.Key, ct);
        }
        catch (MachineKeySecretMissingException ex)
        {
            // Explicit reference broken → visible condition + retry, zero key generated.
            await eventPublisher.PublishAsync(entity, "MachineKeySecretMissing", ex.Message, "Warning", ct);
            entity = await UpdateStatus(entity, "Failed", "MachineKeySecretMissing", ex.Message, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // Build and apply StatefulSet (uses configHash for rolling restart detection)
        var sts = statefulSetBuilder.Build(entity, configHash, machineKeySecretName);
        ChildOwnershipGuard.StampOwner(sts, entity);
        await GuardExistingDataVolumesAsync(entity, sts, ct);
        if (await ApplyStatefulSetAsync(sts, entity, ct))
        {
            // Written first, a ConfigMap pointing masterdatafile/errorlogfile at a volume the
            // refused StatefulSet never mounted was read at the next restart (a crash, or a blank master
            // on a writable root filesystem).
            await ApplyConfigMapAsync(name, ns, confContent, entity, ct);
        }

        // Build and apply Service
        var labels = new Dictionary<string, string> { { "mssql-operator.evariops.com/instance", name } };
        var owner = ownerRef;

        var serviceSpec = entity.Spec.Service ?? new ServiceSpec();
        var svc = serviceBuilder.Build(name, ns, labels, serviceSpec, owner);
        await ApplyServiceAsync(svc, ct);

        // Opt-in PDB — maxUnavailable=0 forces drains to wait for a controlled
        // eviction (combined with the I2 grace period, the pod checkpoints cleanly).
        if (entity.Spec.PodDisruptionBudget?.Enabled == true)
            await ApplyPodDisruptionBudgetAsync(entity, labels, ownerRef, ct);

        // Converge the sa login BEFORE the serving gate. The gate connects with the
        // Secret's password, so after a Secret rotation it can only fail — and it used to return
        // early, skipping the very ALTER LOGIN that would have made it pass.
        var sa = await saPasswordConverger.ConvergeAsync(entity, [$"{name}-0"], saPassword, ct);
        ConditionOps.Set(entity.Status!.Conditions ??= [], "SaPasswordConverged",
            sa == SaConvergence.Converged ? "True" : "False", sa.ToString(),
            sa == SaConvergence.Converged
                ? "SQL Server accepts the sa password of the referenced Secret"
                : "The sa password of the referenced Secret is not yet applied (see events)");
        await DropLegacySaHashAnnotationAsync(name, ns, ct);

        // The phase must be honest — "Running" only once the pod is Ready AND SQL
        // answers a SELECT 1 through the Service. Until then the instance is Provisioning.
        var endpoint = $"{name}.{ns}.svc.cluster.local:{serviceSpec.Port}";
        var productVersion = await ProbeEngineBuildAsync(name, ns, saPassword, serviceSpec.Port, ct);
        if (productVersion is null)
        {
            // A pod stuck Pending because a hard topology constraint can't be met is
            // reported distinctly (reason SchedulingBlocked) rather than masquerading as a normal
            // "still starting up" wait.
            if (SchedulingDiagnostics.UnschedulableReason(
                    await kubernetesClient.GetAsync<V1Pod>($"{name}-0", ns, ct)) is { } unschedulable)
            {
                await eventPublisher.PublishAsync(entity, "SchedulingBlocked",
                    $"Pod {name}-0 cannot be scheduled: {unschedulable}", "Warning", ct);
                entity = await UpdateStatus(entity, "Provisioning", "SchedulingBlocked",
                    $"Pod {name}-0 unschedulable: {unschedulable}", ct, endpoint);
                return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(15));
            }

            entity = await UpdateStatus(entity, "Provisioning", "WaitingForSqlServer",
                "StatefulSet applied; waiting for the pod to be Ready and SQL Server to accept logins", ct, endpoint);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(15));
        }

        // Apply memory alignment if spec.memory is defined
        if (entity.Spec.Memory != null)
        {
            entity = await ApplyMemoryAlignmentAsync(entity, saPassword, ns, name, serviceSpec.Port, ct);
        }

        // Full-text visibility. The operator was completely blind to it — which is why pods that
        // took minutes to become usable after a restart were so hard to
        // diagnose: the readiness probe answered SELECT 1 happily while the engine was being crushed by
        // population crawls resumed at startup. Now the state is in the CR.
        await ReconcileFullTextStatusAsync(entity, saPassword, ns, name, serviceSpec.Port, ct);

        // Backup Service Master Key
        await BackupSmkAsync(entity, saPassword, ns, name, ct);

        // CiliumNetworkPolicy. Before the final status write — the condition used to be set
        // after it, so it was never persisted.
        if (entity.Spec.NetworkPolicy?.Enabled == true)
        {
            // CNPs apply post-DNAT — the ingress port must be the CONTAINER port
            // (1433), not the Service port, or the policy blocks all traffic the moment
            // service.port is customized.
            var netpol = networkPolicyBuilder.BuildForInstance(name, ns, labels, SqlEndpoints.ContainerPort, owner,
                entity.Spec.NetworkPolicy.AllowedNamespaces, Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? ns);
            try
            {
                var existing = await kubernetesClient.GetAsync<GenericCiliumNetworkPolicy>(netpol.Metadata.Name, ns, ct);
                if (existing != null)
                {
                    netpol.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetesClient.UpdateAsync(netpol, ct);
                }
                else
                {
                    await kubernetesClient.CreateAsync(netpol, ct);
                }
                ConditionOps.Set(entity.Status!.Conditions ??= [], "NetworkPolicyApplied", "True",
                    "Applied", $"CiliumNetworkPolicy {netpol.Metadata.Name} applied");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Fail-visible — believing an instance is isolated when nothing was
                // applied is worse than no policy at all.
                logger.LogWarning(ex, "Failed to apply CiliumNetworkPolicy for {Name}", name);
                ConditionOps.Set(entity.Status!.Conditions ??= [], "NetworkPolicyApplied", "False",
                    "ApplyFailed", "The CiliumNetworkPolicy could not be applied (see events)");
                await eventPublisher.PublishAsync(entity, "NetworkPolicyApplyFailed", ex.Message, "Warning", ct);
            }
        }

        // Update status after all side effects have completed
        entity = await UpdateStatus(entity, "Running", "InstanceRunning", "SQL Server instance is running", ct, endpoint, productVersion);

        // Success events only on a real phase transition, not every reconcile.
        if (previousPhase != "Running")
            await eventPublisher.PublishAsync(entity, "InstanceProvisioned", $"SQL Server instance {name} is running", ct: ct);
        logger.LogInformation("SqlInstance {Name} reconciled successfully", name);
        // Periodic requeue so instance drift (pod loss, config drift) is re-detected in steady state
        return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(60));
    }

    public Task<ReconciliationResult<SqlInstance>> DeletedAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("SqlInstance {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlInstance", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }

    /// <summary>Earlier versions stamped an unsalted, truncated SHA-256 of the sa password on
    /// the StatefulSet — readable by anyone allowed to get StatefulSets, an offline dictionary attack
    /// away from the password. Convergence now compares with the retained applied-password Secret.</summary>
    private const string LegacySaPasswordHashAnnotation = "mssql-operator.evariops.com/sa-password-hash";

    private async Task DropLegacySaHashAnnotationAsync(string name, string ns, CancellationToken ct)
    {
        var sts = await kubernetesClient.GetAsync<V1StatefulSet>(name, ns, ct);
        if (sts?.Metadata.Annotations?.Remove(LegacySaPasswordHashAnnotation) != true)
            return;
        try
        {
            await kubernetesClient.UpdateAsync(sts, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not remove the legacy sa password hash annotation of {Name}", name);
        }
    }

    private async Task ApplyPodDisruptionBudgetAsync(SqlInstance entity, Dictionary<string, string> labels, V1OwnerReference ownerRef, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var pdb = new V1PodDisruptionBudget
        {
            ApiVersion = "policy/v1",
            Kind = "PodDisruptionBudget",
            Metadata = new V1ObjectMeta
            {
                Name = $"{entity.Metadata.Name}-pdb",
                NamespaceProperty = ns,
                // managed-by on metadata only; the PDB selector is immutable.
                Labels = new Dictionary<string, string>(labels) { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                OwnerReferences = [ownerRef]
            },
            Spec = new V1PodDisruptionBudgetSpec
            {
                MaxUnavailable = 0,
                Selector = new V1LabelSelector { MatchLabels = labels }
            }
        };

        try
        {
            var existing = await kubernetesClient.GetAsync<V1PodDisruptionBudget>(pdb.Metadata.Name, ns, ct);
            if (existing == null)
                await kubernetesClient.CreateAsync(pdb, ct);
            else
            {
                pdb.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                await kubernetesClient.UpdateAsync(pdb, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply PodDisruptionBudget for {Name}", entity.Metadata.Name);
        }
    }

    private async Task ApplyConfigMapAsync(string name, string ns, string confContent, SqlInstance owner, CancellationToken ct)
    {
        var cmName = $"{name}-mssql-conf";
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = cmName,
                NamespaceProperty = ns,
                // Scoping labels so the owned-resource watch selector covers the ConfigMap.
                Labels = new Dictionary<string, string>
                {
                    [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue,
                    [OperatorLabels.Instance] = name,
                },
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = owner.ApiVersion, Kind = owner.Kind,
                        Name = owner.Metadata.Name, Uid = owner.Metadata.Uid,
                        Controller = true, BlockOwnerDeletion = true
                    }
                ]
            },
            Data = new Dictionary<string, string> { ["mssql.conf"] = confContent }
        };
        ChildOwnershipGuard.StampOwner(cm, owner);

        // Server-side apply with force=true and a single shared field manager never
        // conflicts, so a homonymous CR of the other kind silently rewrote this ConfigMap (content
        // AND ownerReference) on every reconcile. Check ownership before touching it.
        var existing = await kubernetesClient.GetAsync<V1ConfigMap>(cmName, ns, ct);
        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"ConfigMap {ns}/{cmName}");

        // Server-side apply.
        await kubernetesClient.ApplyAsync(cm, ct);
    }

    /// <summary>Applies the StatefulSet; false when the change is held back (engine upgrade awaiting
    /// approval keeps the running image; a restart over the fleet budget waits).</summary>
    private async Task<bool> ApplyStatefulSetAsync(V1StatefulSet sts, SqlInstance owner, CancellationToken ct)
    {
        var ns = sts.Metadata.NamespaceProperty;
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, ns, ct);

        // Never touch a StatefulSet that belongs to another kind of CR. Creating a
        // SqlInstance named like an existing SqlAvailabilityGroup used to reach the storage-expansion
        // path below, patch the AG's PVCs and then orphan-delete the AG's StatefulSet — leaving its
        // pods unmanaged, i.e. gone for good at the first drain.
        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"StatefulSet {ns}/{sts.Metadata.Name}");

        var conditions = owner.Status!.Conditions ??= [];
        if (!await RolloutGate.ApplyAsync(sts, existing, owner.Spec.ImageSelection(), owner.Metadata.Annotations,
                owner.Spec.UpdateStrategy?.Type == "OnDelete", conditions, owner, rolloutBudget, eventPublisher, options, ct))
            return false;

        // Storage GROWTH is orchestrated — patch the PVCs in
        // place (requires allowVolumeExpansion on the StorageClass), then recreate the
        // StatefulSet around the live pods (volumeClaimTemplates are immutable). Shrinking is
        // rejected at admission.
        if (existing != null && await TryExpandVolumeClaimsAsync(existing, sts, ct))
        {
            await kubernetesClient.DeleteStatefulSetOrphanAsync(sts.Metadata.Name, ns, ct);
            // Wait for the apiserver to release the name before re-applying. If it is
            // still there when the wait expires, do NOT apply on top of the dying object (a 422 loop
            // that re-patched and re-deleted on every backoff) — requeue and try again.
            for (var i = 0; i < 20 && await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, ns, ct) != null; i++)
                await Task.Delay(500, ct);
            if (await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, ns, ct) != null)
                throw new StatefulSetNameNotReleasedException(
                    $"StatefulSet {ns}/{sts.Metadata.Name} is still terminating after the storage expansion; retrying shortly.");
        }

        // Server-side apply — no 422 loop on immutable fields, no clobbering of
        // fields owned by other actors (webhooks, sidecar injectors).
        await kubernetesClient.ApplyAsync(sts, ct);
        return true;
    }

    /// <summary>
    /// The PVCs of a StatefulSet are named <c>{template}-{sts}-{ordinal}</c> and outlive the CR (no
    /// ownerReference, no retention policy). A CR of a DIFFERENT kind created on the same name would
    /// therefore start SQL Server on the previous tenant's master and user databases.
    /// Refuse; a same-kind recreate legitimately adopts its own data, but says so out loud.
    /// </summary>
    private async Task GuardExistingDataVolumesAsync(SqlInstance entity, V1StatefulSet sts, CancellationToken ct)
    {
        var ns = sts.Metadata.NamespaceProperty;
        foreach (var template in sts.Spec?.VolumeClaimTemplates ?? [])
        {
            if (template.Metadata?.Name is not { } tplName) continue;
            var pvcName = $"{tplName}-{sts.Metadata.Name}-0";
            var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
            if (pvc is null) continue;

            ChildOwnershipGuard.EnsureOwnedBy(pvc, entity, $"PersistentVolumeClaim {ns}/{pvcName}");

            if (ChildOwnershipGuard.IsStaleSameKindChild(pvc, entity))
                await eventPublisher.PublishAsync(entity, "AdoptedExistingData",
                    $"PersistentVolumeClaim {pvcName} was left behind by a previous SqlInstance of the same name; " +
                    "this instance starts on its existing data files. Delete the PVCs first if you wanted a blank instance.",
                    "Warning", ct);

            // The guard reads owner labels off the PVC, but nothing ever WROTE them —
            // volumeClaimTemplates cannot carry them (template metadata is immutable on a live
            // StatefulSet), so every PVC looked "unstamped legacy" and the refusal above could
            // never fire. Stamp at reconcile time: the next homonym of another kind is then actually
            // refused, and the stale-child adoption event fires once instead of on every pass.
            if (!ChildOwnershipGuard.IsStampedBy(pvc, entity))
            {
                ChildOwnershipGuard.StampOwner(pvc, entity);
                try { await kubernetesClient.UpdateAsync(pvc, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not stamp ownership labels on PVC {Pvc}", pvcName); }
            }
        }
    }

    /// <summary>Grows PVCs whose template size increased. Returns true when the StatefulSet
    /// must be recreated (orphan) to carry the new template.</summary>
    private async Task<bool> TryExpandVolumeClaimsAsync(V1StatefulSet existing, V1StatefulSet desired, CancellationToken ct)
    {
        var ns = desired.Metadata.NamespaceProperty;
        var grown = false;
        var existingClaims = (existing.Spec?.VolumeClaimTemplates ?? [])
            .Where(c => c.Metadata?.Name != null)
            .ToDictionary(c => c.Metadata.Name);

        foreach (var template in desired.Spec?.VolumeClaimTemplates ?? [])
        {
            if (template.Metadata?.Name is null || !existingClaims.TryGetValue(template.Metadata.Name, out var current))
                continue;
            k8s.Models.ResourceQuantity? desiredSize = null;
            k8s.Models.ResourceQuantity? currentSize = null;
            template.Spec?.Resources?.Requests?.TryGetValue("storage", out desiredSize);
            current.Spec?.Resources?.Requests?.TryGetValue("storage", out currentSize);
            if (desiredSize is null || currentSize is null || desiredSize.ToString() == currentSize.ToString())
                continue;

            grown = true;
            var replicas = existing.Spec?.Replicas ?? 1;
            for (var ordinal = 0; ordinal < replicas; ordinal++)
            {
                var pvcName = $"{template.Metadata.Name}-{existing.Metadata.Name}-{ordinal}";
                var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
                if (pvc?.Spec?.Resources?.Requests == null) continue;
                pvc.Spec.Resources.Requests["storage"] = desiredSize;
                await kubernetesClient.UpdateAsync(pvc, ct);
                logger.LogInformation("Expanded PVC {Pvc} to {Size}", pvcName, desiredSize.ToString());
            }
        }

        return grown;
    }

    private async Task ApplyServiceAsync(V1Service svc, CancellationToken ct)
    {
        // SSA preserves clusterIP and third-party fields automatically.
        await kubernetesClient.ApplyAsync(svc, ct);
    }

    /// <summary>Null only when the Secret or its key is absent. An API error used to read as
    /// "not found" and put the instance in phase Failed on a transient hiccup.</summary>
    private Task<string?> ResolveSaPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
        => SecretValues.TryGetAsync(kubernetesClient, entity.Spec.SaPasswordSecretRef, ns, ct);

    /// <summary>Pod Ready (real Ready condition) + the engine answering through the Service.
    /// Returns SERVERPROPERTY('ProductVersion') — the build that is actually serving — or null when
    /// the instance is not serving. It costs the same round-trip the old SELECT 1 did, and it is the
    /// only way to tell a CU upgrade that has landed from one the pod has not picked up yet.</summary>
    private async Task<string?> ProbeEngineBuildAsync(string name, string ns, string saPassword, int port, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<k8s.Models.V1Pod>($"{name}-0", ns, ct);
        var podReady = pod?.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
        if (!podReady)
            return null;

        try
        {
            // The probe profile (3s, no retry) — the full profile (30s × 4 retries) held
            // a reconcile worker for ~2 minutes per unreachable instance, and this gate runs on every
            // single pass.
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                $"{name}.{ns}.svc.cluster.local", port, saPassword, "master", ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductVersion'))";
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SqlInstance {Name} pod is Ready but SQL is not serving yet", name);
            return null;
        }
    }

    private async Task<SqlInstance> UpdateStatus(SqlInstance entity, string phase, string reason, string message,
        CancellationToken ct, string? endpoint = null, string? productVersion = null)
    {
        entity.Status ??= new SqlInstanceStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Version = entity.Spec.Version;
        entity.Status.Edition = entity.Spec.Edition;

        // The desired build, as resolved from the spec. status.image is what the StatefulSet was
        // given; status.productVersion (below) is what answered — they diverge exactly while a CU
        // upgrade is in flight, and that gap is the signal.
        if (SqlServerImages.TryResolve(entity.Spec.ImageSelection(), out var resolution))
        {
            entity.Status.Image = resolution.Image;
            entity.Status.CumulativeUpdate = resolution.CumulativeUpdate;
            entity.Status.BaseOs = resolution.BaseOs;
        }

        // Keep the last build observed when the engine is unreachable this pass: an instance that
        // stopped answering has not gone back to no version at all.
        entity.Status.ProductVersion = productVersion ?? entity.Status.ProductVersion;
        entity.Status.Endpoint = endpoint;
        // Use ConditionOps so LastTransitionTime only moves on an actual status transition. Rebuilding
        // the condition with DateTime.UtcNow every reconcile would make the status byte-unstable,
        // defeating the status-write dedup and causing a self-reconcile hot loop.
        entity.Status.Conditions ??= [];
        ConditionOps.Set(entity.Status.Conditions, "Ready", phase == "Running" ? "True" : "False", reason, message);
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private async Task<SqlInstance> ApplyMemoryAlignmentAsync(SqlInstance entity, string saPassword, string ns, string name, int port, CancellationToken ct)
    {
        var memSpec = entity.Spec.Memory!;
        var totalMb = MemoryCalculator.ParseToMb(memSpec.Total);
        var reserveMb = MemoryCalculator.ParseToMb(memSpec.SystemReserve);
        // runtimeClassName-aware, matching the env var calculation.
        var sqlMaxMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memSpec, entity.Spec.RuntimeClassName);
        var sqlMinMb = MemoryCalculator.CalculateSqlMinMemoryMb(memSpec);

        try
        {
            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);

            var currentConfig = await sqlServerManager.GetCurrentConfigAsync(conn, ct);
            var currentMax = currentConfig.GetValueOrDefault("max server memory (MB)", 0);
            var currentMin = currentConfig.GetValueOrDefault("min server memory (MB)", 0);
            var aligned = currentMax == sqlMaxMb && currentMin == sqlMinMb;

            if (!aligned)
            {
                if (currentMax != sqlMaxMb)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "max server memory (MB)", sqlMaxMb, ct);
                    logger.LogInformation("Applied max server memory = {MaxMb} MB on {Name} (pod limit: {TotalMb} MB, reserve: {ReserveMb} MB)",
                        sqlMaxMb, name, totalMb, reserveMb);
                }

                if (currentMin != sqlMinMb && sqlMinMb > 0)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "min server memory (MB)", sqlMinMb, ct);
                    logger.LogInformation("Applied min server memory = {MinMb} MB on {Name}", sqlMinMb, name);
                }

                await sqlServerManager.ReconfigureAsync(conn, ct: ct);

                await eventPublisher.PublishAsync(entity, "MemoryConfigured",
                    $"max server memory set to {sqlMaxMb} MB (pod limit: {totalMb} MB, system reserve: {reserveMb} MB)", ct: ct);
                aligned = true;
            }

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = aligned
            };
            // Dedicated condition — alignment problems stay visible without
            // degrading the phase while the instance is serving.
            ConditionOps.Set(entity.Status.Conditions ??= [], "MemoryAligned", "True",
                "Aligned", $"max server memory = {sqlMaxMb} MB");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply memory alignment on {Name} — will retry", name);
            entity.Status ??= new SqlInstanceStatus();
            ConditionOps.Set(entity.Status.Conditions ??= [], "MemoryAligned", "False",
                "AlignmentFailed", "max server memory could not be aligned (see events)");

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = false
            };

            await eventPublisher.PublishAsync(entity, "MemoryDriftDetected",
                $"max server memory should be {sqlMaxMb} MB but alignment failed: {ex.Message}", "Warning", ct);
        }

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return entity;
    }

    private async Task BackupSmkAsync(SqlInstance entity, string saPassword, string ns, string name, CancellationToken ct)
    {
        try
        {
            // Resolve or generate SMK backup password
            var smkPassword = await ResolveSmkPasswordAsync(entity, ns, ct);
            if (smkPassword == null) return;

            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            var smkSecretName = $"{name}-smk-backup";
            var existingSmk = await kubernetesClient.GetAsync<V1Secret>(smkSecretName, ns, ct);
            // A backup taken by a legacy operator still carries an ownerReference —
            // migrate it to the retained shape whether or not it needs refreshing.
            if (existingSmk is not null)
                existingSmk = await KeyMaterialSecrets.AdoptAsync(kubernetesClient, existingSmk, entity, logger, ct);

            // The backup used to be taken ONCE and never looked at again. If the Service
            // Master Key changed afterwards (regeneration, a restore, a machine-key change), the stored
            // backup silently stopped matching the live key — the single artifact that could recover
            // the TDE certificates and stored credentials was quietly worthless, and nothing said so.
            // The key's modify_date is compared against the date of the backup we hold.
            var smkModifiedAt = await smkManager.GetSmkModifyDateAsync(conn, ct);
            if (existingSmk?.Data != null && existingSmk.Data.ContainsKey("smk.bak"))
            {
                if (!IsSmkBackupStale(existingSmk, smkModifiedAt))
                {
                    entity.Status ??= new SqlInstanceStatus();
                    ConditionOps.Set(entity.Status.Conditions ??= [], "SmkBackupSucceeded", "True", "BackedUp",
                        $"Service master key backed up to secret {smkSecretName}");
                    return; // up to date
                }

                logger.LogWarning(
                    "The service master key of {Name} changed after its backup was taken; refreshing the backup secret {Secret}",
                    name, smkSecretName);
                await eventPublisher.PublishAsync(entity, "SmkBackupStale",
                    "The service master key changed after the stored backup was taken: the backup could no longer restore this " +
                    "instance. Taking a fresh one.", "Warning", ct);
            }

            // Backup SMK to temp file inside the pod, read it, create secret
            var backupPath = SqlTempPaths.Create("bak");
            await smkManager.BackupSmkAsync(conn, backupPath, smkPassword, ct);

            // Read the backup file from the pod via SQL
            var smkBytes = await ReadFileViaSqlAsync(conn, backupPath, ct);
            if (smkBytes == null || smkBytes.Length == 0)
            {
                logger.LogWarning("SMK backup file is empty for {Name}", name);
                return;
            }

            // Create the SMK backup secret. No ownerReference — the backup must outlive
            // the CR, since the data it protects (on the PVCs) does.
            var smkSecret = KeyMaterialSecrets.Build(smkSecretName, ns, entity, new Dictionary<string, byte[]>
            {
                ["smk.bak"] = smkBytes
            });
            // Stamp WHEN the key we backed up was last modified, so a later reconcile can
            // tell whether the backup still matches the live key.
            if (smkModifiedAt is { } modified)
                smkSecret.Metadata.Annotations![SmkModifyDateAnnotation] = modified.ToString("O");

            if (existingSmk is null)
            {
                await kubernetesClient.CreateAsync(smkSecret, ct);
                logger.LogInformation("Created SMK backup secret {Secret} for {Name}", smkSecretName, name);
            }
            else
            {
                existingSmk.Data = smkSecret.Data;
                existingSmk.Metadata.Annotations ??= new Dictionary<string, string>();
                if (smkModifiedAt is { } m)
                    existingSmk.Metadata.Annotations[SmkModifyDateAnnotation] = m.ToString("O");
                await kubernetesClient.UpdateAsync(existingSmk, ct);
                logger.LogWarning("Refreshed the stale SMK backup secret {Secret} for {Name}", smkSecretName, name);
                await eventPublisher.PublishAsync(entity, "SmkBackupRefreshed",
                    "The service master key backup was refreshed to match the current key.", ct: ct);
            }

            entity.Status ??= new SqlInstanceStatus();
            ConditionOps.Set(entity.Status.Conditions ??= [], "SmkBackupSucceeded", "True",
                "BackedUp", $"Service master key backed up to secret {smkSecretName}");

            // The temp .bak stays in the pod's ephemeral /tmp under a unique name: SQL Server on
            // Linux has no native command to delete an arbitrary file (xp_cmdshell is unavailable),
            // so cleanup is left to the pod lifecycle.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to backup SMK for {Name} — will retry on next reconciliation", name);
            entity.Status ??= new SqlInstanceStatus();
            ConditionOps.Set(entity.Status.Conditions ??= [], "SmkBackupSucceeded", "False",
                "BackupFailed", "The service master key could not be backed up (see the operator logs)");
        }
    }

    /// <summary>
    /// Surfaces the full-text state of the instance: whether the FTS package is even
    /// installed, whether populations are running right now, and whether <c>upgrade_option</c> is set
    /// to a value that would repopulate every catalogue on the next engine upgrade.
    ///
    /// This is the diagnostic that was missing entirely: an instance whose restart takes twenty
    /// minutes because it resumed an interrupted FULL population looked, to Kubernetes and to the
    /// operator, exactly like an instance that was simply slow.
    /// </summary>
    private async Task ReconcileFullTextStatusAsync(
        SqlInstance entity, string saPassword, string ns, string name, int port, CancellationToken ct)
    {
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                SqlEndpoints.ServiceFqdn(name, ns), port, saPassword, "master", ct);
            var status = await fullTextManager.GetStatusAsync(conn, ct);

            entity.Status ??= new SqlInstanceStatus();
            entity.Status.FullText = new FullTextStatus
            {
                Installed = status.Installed,
                UpgradeOption = status.Installed ? status.UpgradeOption : null,
                ActivePopulations = status.ActivePopulations.Count,
            };

            if (!status.Installed)
            {
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextPopulationActive", "False",
                    "FullTextNotInstalled", "Full-Text Search is not installed on this instance");
                return;
            }

            if (status.HasActivePopulations)
            {
                var first = status.ActivePopulations[0];
                var message =
                    $"{status.ActivePopulations.Count} full-text population(s) running (e.g. {first.PopulationType} on " +
                    $"database {first.DatabaseName}, status {first.Status}). The instance is usable but under crawl load; " +
                    "avoid restarting it now — an interrupted population is resumed at the next startup, on a cold cache.";
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextPopulationActive", "True",
                    "PopulationRunning", message);
                await eventPublisher.PublishAsync(entity, "FullTextPopulationActive", message, ct: ct);
            }
            else
            {
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextPopulationActive", "False",
                    "NoPopulationRunning", "No full-text population is running");
            }

            // Rebuild/Reset repopulate EVERY catalogue on the first boot after an engine
            // upgrade — the full-text storm, scheduled for the worst possible moment.
            // on SQL Server 2022+ the option is removed altogether (NotApplicable) — there is nothing
            // to configure, and the pre-2022 advice (Import = 2) no longer exists there.
            if (status.UpgradeOption.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextUpgradeOptionSafe", "True",
                    "Import", "Full-text indexes will be imported (not rebuilt) on the next engine upgrade");
            }
            else if (status.UpgradeOption.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase))
            {
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextUpgradeOptionSafe", "True",
                    "NotApplicable", "SQL Server 2022+ removed sp_fulltext_service 'upgrade_option'; there is nothing to configure");
            }
            else if (!status.UpgradeOption.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var message =
                    $"sp_fulltext_service 'upgrade_option' is {status.UpgradeOption}: the next SQL Server upgrade will " +
                    "repopulate every full-text catalogue on first boot (hours of crawl on a cold cache). Set it to Import " +
                    "before any CU campaign: EXEC sp_fulltext_service 'upgrade_option', 2.";
                ConditionOps.Set(entity.Status.Conditions ??= [], "FullTextUpgradeOptionSafe", "False",
                    "RepopulateOnUpgrade", message);
                await eventPublisher.PublishAsync(entity, "FullTextUpgradeOptionUnsafe", message, "Warning", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the full-text state of {Name}", name);
        }
    }

    /// <summary>When the Service Master Key we backed up was last modified.</summary>
    internal const string SmkModifyDateAnnotation = "mssql-operator.evariops.com/smk-modify-date";

    /// <summary>
    /// True when the SMK changed after the stored backup was taken — the backup can no longer restore
    /// this instance. An unstamped backup (taken by an earlier operator version) is
    /// treated as stale ONLY if we can read the key's modify date, so a re-stamp happens once.
    /// </summary>
    private static bool IsSmkBackupStale(V1Secret backup, DateTime? smkModifiedAt)
    {
        if (smkModifiedAt is not { } modified)
            return false; // cannot tell — do not churn the backup

        if (backup.Metadata?.Annotations is not { } annotations
            || !annotations.TryGetValue(SmkModifyDateAnnotation, out var raw)
            || !DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var backedUpFor))
            return true; // unstamped: refresh once so the guarantee becomes verifiable

        // Second-level tolerance: sys.symmetric_keys stores a datetime, the annotation a round-trip string.
        return modified > backedUpFor.AddSeconds(1);
    }

    private async Task<string?> ResolveSmkPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SmkBackupPasswordSecretRef != null)
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SmkBackupPasswordSecretRef.Name, ns, ct);
            if (secret?.Data != null && secret.Data.TryGetValue(entity.Spec.SmkBackupPasswordSecretRef.Key, out var bytes))
                return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // Generate and store a random password
        var passwordSecretName = $"{entity.Metadata.Name}-smk-backup-password";
        var existing = await kubernetesClient.GetAsync<V1Secret>(passwordSecretName, ns, ct);
        if (existing?.Data != null && existing.Data.TryGetValue("password", out var existingBytes))
        {
            // Migrate a legacy password Secret (it still carried an ownerReference).
            await KeyMaterialSecrets.AdoptAsync(kubernetesClient, existing, entity, logger, ct);
            return System.Text.Encoding.UTF8.GetString(existingBytes);
        }

        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        // No ownerReference — losing this password with the CR makes the SMK backup
        // (which survives on its own) undecryptable, i.e. worthless.
        var newSecret = KeyMaterialSecrets.Build(passwordSecretName, ns, entity, new Dictionary<string, byte[]>
        {
            ["password"] = System.Text.Encoding.UTF8.GetBytes(password)
        });

        await kubernetesClient.CreateAsync(newSecret, ct);
        logger.LogInformation("Generated SMK backup password secret {Secret}", passwordSecretName);
        return password;
    }

    private static async Task<byte[]?> ReadFileViaSqlAsync(Microsoft.Data.SqlClient.SqlConnection conn, string filePath, CancellationToken ct)
    {
        SqlTempPaths.EnsureSafe(filePath);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[];
    }

}
