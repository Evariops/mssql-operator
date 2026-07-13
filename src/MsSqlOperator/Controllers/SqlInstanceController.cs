using k8s;
using k8s.Models;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
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
    ISqlUserManager sqlUserManager,
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
        logger.LogInformation("Reconciling SqlInstance {Name} in {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);

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

        // Resolve SqlConfiguration if referenced
        SqlConfiguration? sqlConfig = null;
        if (entity.Spec.ConfigurationRef != null)
        {
            sqlConfig = await kubernetesClient.GetAsync<SqlConfiguration>(entity.Spec.ConfigurationRef.Name, ns, ct);
        }

        // Build and apply ConfigMap for mssql.conf
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, entity.Spec.Memory, entity.Spec.Storage, entity.Spec.RuntimeClassName);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);
        await ApplyConfigMapAsync(name, ns, confContent, entity, ct);

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
        await ApplyStatefulSetAsync(sts, entity, ct);

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
        await ReconcileSaPasswordAsync(entity, ns, name, saPassword, serviceSpec.Port, ct);

        // The phase must be honest — "Running" only once the pod is Ready AND SQL
        // answers a SELECT 1 through the Service. Until then the instance is Provisioning.
        var endpoint = $"{name}.{ns}.svc.cluster.local:{serviceSpec.Port}";
        if (!await IsInstanceServingAsync(name, ns, saPassword, serviceSpec.Port, ct))
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

        // Backup Service Master Key
        await BackupSmkAsync(entity, saPassword, ns, name, ct);

        // Update status after all side effects have completed
        entity = await UpdateStatus(entity, "Running", "InstanceRunning", "SQL Server instance is running", ct, endpoint);

        // Apply SqlConfiguration (sp_configure, traceflags, tempdb, databasemail) if referenced
        // The SqlConfiguration's own reconciliation loop handles applying to target instances.
        // Here we just ensure the config was resolved for the ConfigMap generation.
        if (sqlConfig != null)
        {
            logger.LogInformation("SqlConfiguration {Config} resolved for instance {Name}", sqlConfig.Metadata.Name, name);
        }

        // CiliumNetworkPolicy
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
            catch (Exception ex)
            {
                // Fail-visible — believing an instance is isolated when nothing was
                // applied is worse than no policy at all.
                logger.LogWarning(ex, "Failed to apply CiliumNetworkPolicy for {Name}", name);
                ConditionOps.Set(entity.Status!.Conditions ??= [], "NetworkPolicyApplied", "False",
                    "ApplyFailed", ex.Message);
                await eventPublisher.PublishAsync(entity, "NetworkPolicyApplyFailed", ex.Message, "Warning", ct);
            }
        }

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

    private const string SaPasswordHashAnnotation = "mssql-operator.evariops.com/sa-password-hash";

    /// <summary>
    /// Converges the SQL Server <c>sa</c> login to the value in the Secret, including the
    /// case the previous implementation could not handle at all.
    ///
    /// The image reads <c>MSSQL_SA_PASSWORD</c> only on FIRST boot, so rotating the Secret leaves SQL
    /// Server on the old password. The convergence step existed — but it ran AFTER a gate that opens a
    /// connection with the NEW password, which of course fails with 18456. The instance therefore went
    /// to Provisioning and the ALTER LOGIN that would have fixed it was never reached: the rotation was
    /// unconvergeable by any path. Worse, at the next pod restart the readiness probe (which logs in
    /// with the env var, i.e. the new password) failed forever — the pod never became Ready again and
    /// the Service lost its only endpoint.
    ///
    /// The fix has three parts: run BEFORE the serving gate; connect to the pod IP (a NotReady pod has
    /// no Service endpoint, but its IP answers); and keep the last successfully applied password in a
    /// retained Secret so the operator can still authenticate after the referenced Secret changed.
    /// </summary>
    private async Task ReconcileSaPasswordAsync(SqlInstance entity, string ns, string name, string saPassword, int port, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(saPassword)))[..16];

        var sts = await kubernetesClient.GetAsync<V1StatefulSet>(name, ns, ct);
        if (sts == null) return;
        if (sts.Metadata.Annotations != null && sts.Metadata.Annotations.TryGetValue(SaPasswordHashAnnotation, out var appliedHash) && appliedHash == hash)
        {
            await PersistAppliedSaPasswordAsync(entity, ns, name, saPassword, ct); // first upgrade from a legacy operator
            return; // already converged
        }

        var pod = await kubernetesClient.GetAsync<V1Pod>($"{name}-0", ns, ct);
        var podIp = pod?.Status?.PodIP;
        if (string.IsNullOrEmpty(podIp))
        {
            logger.LogDebug("SqlInstance {Name}: pod has no IP yet; deferring sa password convergence", name);
            return;
        }

        var converged = false;
        try
        {
            // Case 1: SQL already has the Secret's password (first boot, or an earlier convergence).
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                podIp, SqlEndpoints.ContainerPort, saPassword, "master", ct);
            await sqlUserManager.AlterLoginPasswordAsync(conn, "sa", saPassword, ct);
            converged = true;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex,
                "SqlInstance {Name}: sa login refused with the Secret's password — the Secret was rotated after first boot; " +
                "attempting to converge with the last applied password", name);
        }

        if (!converged)
        {
            // Case 2: the Secret was rotated. Authenticate with the password we last applied and
            // ALTER LOGIN to the new one — the only path that can converge a rotated Secret.
            var previous = await ReadAppliedSaPasswordAsync(ns, name, ct);
            if (previous is null || previous == saPassword)
            {
                await eventPublisher.PublishAsync(entity, "SaPasswordConvergenceFailed",
                    "The sa login does not accept the password in the referenced Secret, and the operator has no record of the " +
                    "previously applied one (it was rotated before this operator version, or the sa password was changed out of band). " +
                    "Set sa to the Secret's value manually (ALTER LOGIN [sa] WITH PASSWORD = …) — until then the readiness probe, " +
                    "which logs in with the Secret's value, will keep the pod out of the Service.", "Warning", ct);
                return;
            }

            try
            {
                await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                    podIp, SqlEndpoints.ContainerPort, previous, "master", ct);
                await sqlUserManager.AlterLoginPasswordAsync(conn, "sa", saPassword, ct);
                converged = true;
                await eventPublisher.PublishAsync(entity, "SaPasswordRotated",
                    "The sa login was converged to the rotated Secret value", ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not converge sa password for {Name} with the previously applied password; will retry", name);
                return;
            }
        }

        await PersistAppliedSaPasswordAsync(entity, ns, name, saPassword, ct);

        sts.Metadata.Annotations ??= new Dictionary<string, string>();
        sts.Metadata.Annotations[SaPasswordHashAnnotation] = hash;
        try
        {
            await kubernetesClient.UpdateAsync(sts, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist sa password hash annotation for {Name}", name);
        }
    }

    private static string AppliedSaSecretName(string instance) => $"{instance}-sa-applied";

    private async Task<string?> ReadAppliedSaPasswordAsync(string ns, string name, CancellationToken ct)
    {
        var secret = await kubernetesClient.GetAsync<V1Secret>(AppliedSaSecretName(name), ns, ct);
        return secret?.Data is { } data && data.TryGetValue("password", out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;
    }

    /// <summary>Records the sa password SQL Server actually has, so a later rotation of the referenced
    /// Secret is convergeable. Retained like the other key material: the
    /// data on the PVCs is protected by it, so it must outlive the CR.</summary>
    private async Task PersistAppliedSaPasswordAsync(SqlInstance entity, string ns, string name, string saPassword, CancellationToken ct)
    {
        var secretName = AppliedSaSecretName(name);
        var data = new Dictionary<string, byte[]> { ["password"] = System.Text.Encoding.UTF8.GetBytes(saPassword) };
        try
        {
            var existing = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);
            if (existing is null)
            {
                await kubernetesClient.CreateAsync(KeyMaterialSecrets.Build(secretName, ns, entity, data), ct);
                return;
            }

            if (existing.Data is { } d && d.TryGetValue("password", out var current)
                && System.Text.Encoding.UTF8.GetString(current) == saPassword)
                return;

            existing.Data = data;
            await kubernetesClient.UpdateAsync(existing, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the applied sa password for {Name}; a future rotation may need manual convergence", name);
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

    private async Task ApplyStatefulSetAsync(V1StatefulSet sts, SqlInstance owner, CancellationToken ct)
    {
        var ns = sts.Metadata.NamespaceProperty;
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, ns, ct);

        // Never touch a StatefulSet that belongs to another kind of CR. Creating a
        // SqlInstance named like an existing SqlAvailabilityGroup used to reach the storage-expansion
        // path below, patch the AG's PVCs and then orphan-delete the AG's StatefulSet — leaving its
        // pods unmanaged, i.e. gone for good at the first drain.
        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"StatefulSet {ns}/{sts.Metadata.Name}");

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

    private async Task<string?> ResolveSaPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SaPasswordSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            var key = entity.Spec.SaPasswordSecretRef.Key;
            return secret.Data.TryGetValue(key, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pod Ready (real Ready condition) + SELECT 1 via the Service.</summary>
    private async Task<bool> IsInstanceServingAsync(string name, string ns, string saPassword, int port, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<k8s.Models.V1Pod>($"{name}-0", ns, ct);
        var podReady = pod?.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
        if (!podReady)
            return false;

        try
        {
            // The probe profile (3s, no retry) — the full profile (30s × 4 retries) held
            // a reconcile worker for ~2 minutes per unreachable instance, and this gate runs on every
            // single pass.
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                $"{name}.{ns}.svc.cluster.local", port, saPassword, "master", ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SqlInstance {Name} pod is Ready but SQL is not serving yet", name);
            return false;
        }
    }

    private async Task<SqlInstance> UpdateStatus(SqlInstance entity, string phase, string reason, string message, CancellationToken ct, string? endpoint = null)
    {
        entity.Status ??= new SqlInstanceStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Version = entity.Spec.Version;
        entity.Status.Edition = entity.Spec.Edition;
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
                "AlignmentFailed", ex.Message);

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
            await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);

            // Check if backup already exists
            var smkSecretName = $"{name}-smk-backup";
            var existingSmk = await kubernetesClient.GetAsync<V1Secret>(smkSecretName, ns, ct);
            if (existingSmk?.Data != null && existingSmk.Data.ContainsKey("smk.bak"))
            {
                return; // Already backed up
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

            await kubernetesClient.CreateAsync(smkSecret, ct);
            logger.LogInformation("Created SMK backup secret {Secret} for {Name}", smkSecretName, name);
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
                "BackupFailed", ex.Message);
        }
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
            return System.Text.Encoding.UTF8.GetString(existingBytes);

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
