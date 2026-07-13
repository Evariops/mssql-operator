using k8s;
using k8s.Models;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlAvailabilityGroup), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1PodDisruptionBudget), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
[EntityRbac(typeof(V1StorageClass), Verbs = RbacVerb.Get | RbacVerb.List)]
public class SqlAvailabilityGroupController(
    ILogger<SqlAvailabilityGroupController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder,
    IMssqlConfBuilder mssqlConfBuilder,
    IMachineKeyManager machineKeyManager,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IAgPodManager agPodManager,
    IAgHealthChecker agHealthChecker,
    IAgFailoverOrchestrator failoverOrchestrator,
    IAgRollingUpdateOrchestrator rollingUpdateOrchestrator,
    ISqlServerManager sqlServerManager,
    IServiceMasterKeyManager smkManager,
    Services.Metrics.OperatorMetrics metrics) : IEntityController<SqlAvailabilityGroup>
{
    private const string CertNamePrefix = "HadrCert_";
    private const string LoginPrefix = "hadr_";

    public async Task<ReconciliationResult<SqlAvailabilityGroup>> ReconcileAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlAvailabilityGroup {Name} (phase={Phase})", entity.Metadata.Name, entity.Status?.Phase ?? "null");

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var edition = entity.Spec.Edition;
        var isStandard = edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);

        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for AG {Name}", name);
            // Failure is a condition, not a phase. The old UpdatePhase("Failed", …)
            // trapped the AG — the phase switch below has no "Failed" arm, so once persisted the AG
            // never reconciled again, even after the secret reappeared. MarkNotReady keeps the real
            // operational phase and surfaces the cause via Ready=False; the next reconcile self-heals.
            entity = await MarkNotReady(entity, "SaPasswordNotFound", "SA password secret not found", ct);
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromSeconds(30));
        }

        var phase = entity.Status?.Phase ?? "Pending";

        // "Failed" is no longer a phase the operator writes, but AGs persisted by
        // an earlier version still carry it — and the switch below has no arm for it, so they would
        // never reconcile again. Migrate them once, to the phase that reflects reality.
        if (phase == "Failed")
        {
            var sts = await kubernetesClient.GetAsync<V1StatefulSet>(name, ns, ct);
            phase = sts is null ? "Pending" : "Degraded";
            entity.Status!.Phase = phase;
            await eventPublisher.PublishAsync(entity, "LegacyFailedPhaseMigrated",
                $"AG was stuck in the legacy terminal phase Failed; resuming reconciliation in phase {phase}", "Warning", ct);
            logger.LogWarning("AG {Name}: migrated legacy Failed phase to {Phase}", name, phase);
        }

        entity = phase switch
        {
            "Pending" => await ReconcilePendingAsync(entity, saPassword, isStandard, ct),
            "Initializing" => await ReconcileInitializingAsync(entity, saPassword, isStandard, ct),
            "Running" or "Degraded" => await ReconcileRunningAsync(entity, saPassword, isStandard, ct),
            "FailingOver" => await ReconcileRunningAsync(entity, saPassword, isStandard, ct),
            _ => entity
        };

        // If still initializing/waiting for pods, requeue in 15s
        var currentPhase = entity.Status?.Phase;
        // "WaitingForPods" is a reason, never a phase — the old check was dead.
        if (currentPhase is "Initializing" or "Pending")
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromSeconds(15));

        // Steady-state phases must be re-reconciled periodically: a primary that dies
        // without touching the CR would otherwise never be re-detected (no event watch fires).
        if (currentPhase is "Running" or "Degraded" or "FailingOver")
        {
            var interval = entity.Spec.Failover?.HealthCheckIntervalSeconds ?? 10;
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromSeconds(Math.Max(5, interval)));
        }

        // Unknown phases (should not occur — "Failed" is no longer a persisted phase):
        // slow requeue as a safety net.
        return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Represents a failure as a Ready=False condition WITHOUT persisting a
    /// terminal "Failed" phase. The phase switch in <see cref="ReconcileAsync"/> only routes
    /// Pending/Initializing/Running/Degraded/FailingOver; a persisted "Failed" therefore stopped
    /// the AG reconciling forever (health, failover, rolling update all frozen) even once the cause
    /// was fixed. Here the AG keeps its real operational phase — an already-running AG whose
    /// machine-key secret vanished is Degraded (still routed to ReconcileRunningAsync, so it
    /// self-heals when the secret returns); a not-yet-started one stays Pending.
    /// </summary>
    private async Task<SqlAvailabilityGroup> MarkNotReady(SqlAvailabilityGroup entity, string reason, string message, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupStatus();
        var current = entity.Status.Phase;
        entity.Status.Phase = current is "Running" or "Degraded" or "FailingOver"
            ? "Degraded"
            : string.IsNullOrEmpty(current) ? "Pending" : current;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Conditions ??= [];
        ConditionOps.Set(entity.Status.Conditions, "Ready", "False", reason, message);
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    // ── Phase: Pending → Initializing ─────────────────────

    private async Task<SqlAvailabilityGroup> ReconcilePendingAsync(SqlAvailabilityGroup entity, string saPassword, bool isStandard, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var replicaCount = entity.Spec.Replicas.Count;

        try
        {
            await ApplyAgInfrastructureAsync(entity, isStandard, ct);
        }
        catch (MachineKeySecretMissingException ex)
        {
            // Explicit reference broken → visible condition + retry, zero key generated.
            // Condition, not a "Failed" phase — stays Pending and self-heals when the secret appears.
            await eventPublisher.PublishAsync(entity, "MachineKeySecretMissing", ex.Message, "Warning", ct);
            return await MarkNotReady(entity, "MachineKeySecretMissing", ex.Message, ct);
        }

        await eventPublisher.PublishAsync(entity, "AgInfraCreated", $"K8s resources created for AG {name} ({entity.Spec.Edition})", ct: ct);
        return await UpdatePhase(entity, "Initializing", "InfrastructureReady", $"StatefulSet, services created. Waiting for {replicaCount} pods.", ct);
    }

    /// <summary>
    /// Builds and applies every owned AG resource (ConfigMap, machine key, StatefulSet, PDB,
    /// services, network policy). Idempotent. Also invoked from the Running phase so
    /// drift is repaired and image/config changes actually reach the StatefulSet — this is what
    /// makes the AG rolling-update orchestrator operative (it reads sts.Status.UpdateRevision,
    /// which never changed before because Running never re-applied the STS).
    /// </summary>
    private async Task ApplyAgInfrastructureAsync(SqlAvailabilityGroup entity, bool isStandard, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // 1. Resolve SqlConfiguration and build ConfigMap
        SqlConfiguration? sqlConfig = null;
        var tmpl = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (tmpl.ConfigurationRef != null)
        {
            sqlConfig = await kubernetesClient.GetAsync<SqlConfiguration>(tmpl.ConfigurationRef.Name, ns, ct);
        }
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, tmpl.Memory, tmpl.Storage, tmpl.RuntimeClassName);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);
        await ApplyConfigMapAsync(name, ns, confContent, entity, ct);

        // 1b. Ensure machine key secret (shared across all AG replicas).
        // MachineKeySecretMissingException propagates to the caller.
        var owner = BuildOwnerRef(entity);
        var machineKeySecretName = await machineKeyManager.EnsureMachineKeySecretAsync(
            name, ns, owner,
            entity.Spec.MachineKeySecretRef?.Name, entity.Spec.MachineKeySecretRef?.Key, ct);

        // 2. Create StatefulSet
        var sts = statefulSetBuilder.BuildForAg(entity, configHash, machineKeySecretName);
        // Override to OnDelete for AG-aware rolling updates
        sts.Spec.UpdateStrategy = new V1StatefulSetUpdateStrategy { Type = "OnDelete" };
        await ApplyStatefulSetAsync(sts, ct);

        // 2b. PodDisruptionBudget: never let a drain evict more than one AG replica at a time
        await ApplyPodDisruptionBudgetAsync(entity, ct);

        // 2c. Warn if the storage class would delete AG data on PVC removal
        await CheckStorageReclaimPolicyAsync(entity, ct);

        // 3. Create Headless Service
        var labels = BuildAgLabels(name);
        var headless = serviceBuilder.BuildHeadless($"{name}-headless", ns, labels, 1433, owner);
        await ApplyServiceAsync(headless, ct);

        // 3. Create R/W Listener Service (selector requires the confirmed writer, closing the
        // split-brain window where two pods transiently carry ag-role=primary).
        if (entity.Spec.Listener != null)
        {
            var rwLabels = new Dictionary<string, string>(labels)
            {
                ["mssql-operator.evariops.com/ag-role"] = "primary",
                ["mssql-operator.evariops.com/listener-ready"] = "writer",
            };
            var listenerSvc = serviceBuilder.Build(
                $"{name}-listener", ns, rwLabels,
                entity.Spec.Listener.Service ?? new ServiceSpec { Port = entity.Spec.Listener.Port },
                owner);
            await ApplyServiceAsync(listenerSvc, ct);
        }

        // 4. Create RO Listener Service (Enterprise only). Selector requires a readable,
        // read-intent-ready secondary so traffic never lands on a non-readable/seeding replica.
        if (!isStandard && entity.Spec.ReadOnlyListener != null)
        {
            var roLabels = new Dictionary<string, string>(labels)
            {
                ["mssql-operator.evariops.com/ag-role"] = "secondary",
                ["mssql-operator.evariops.com/readable"] = "true",
                ["mssql-operator.evariops.com/read-intent-ready"] = "true",
            };
            var roSvc = serviceBuilder.Build(
                $"{name}-ro", ns, roLabels,
                entity.Spec.ReadOnlyListener.Service ?? new ServiceSpec { Port = entity.Spec.ReadOnlyListener.Port },
                owner);
            await ApplyServiceAsync(roSvc, ct);
        }

        // 5. CiliumNetworkPolicy
        if (entity.Spec.NetworkPolicy?.Enabled == true)
        {
            var endpointPort = entity.Spec.Endpoint?.Port ?? 5022;
            var netpol = networkPolicyBuilder.BuildForAg(name, ns, labels, 1433, endpointPort, owner,
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
            }
            catch (Exception ex)
            {
                // Fail-visible — never let the user believe the AG is isolated.
                logger.LogWarning(ex, "Failed to apply CiliumNetworkPolicy for AG {Name}", name);
                entity.Status ??= new SqlAvailabilityGroupStatus();
                ConditionOps.Set(entity.Status.Conditions ??= [], "NetworkPolicyApplied", "False",
                    "ApplyFailed", ex.Message);
                await eventPublisher.PublishAsync(entity, "NetworkPolicyApplyFailed", ex.Message, "Warning", ct);
            }
        }

    }


    // ── Phase: Initializing → Running ─────────────────────

    private async Task<SqlAvailabilityGroup> ReconcileInitializingAsync(SqlAvailabilityGroup entity, string saPassword, bool isStandard, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var replicaCount = entity.Spec.Replicas.Count;
        var headlessService = $"{name}-headless";
        var endpointSpec = entity.Spec.Endpoint ?? new AgEndpointSpec();

        // 1. Wait for all pods to be ready
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            if (!await agPodManager.IsPodReadyAsync(podName, ns, ct))
            {
                // Surface an unsatisfiable DoNotSchedule topology spread (e.g. more
                // replicas than zones) instead of a silent WaitingForPods loop.
                var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
                if (SchedulingDiagnostics.UnschedulableReason(pod) is { } unschedulable)
                {
                    await eventPublisher.PublishAsync(entity, "SchedulingBlocked",
                        $"Pod {podName} cannot be scheduled: {unschedulable}", "Warning", ct);
                    return await UpdatePhase(entity, "Initializing", "SchedulingBlocked",
                        $"Pod {podName} unschedulable: {unschedulable}", ct);
                }
                logger.LogInformation("Pod {Pod} not ready yet, will retry in 15s", podName);
                return await UpdatePhase(entity, "Initializing", "WaitingForPods",
                    $"Pod {podName} not ready, waiting...", ct);
            }
        }

        // Resolve secrets. When a master-key password Secret is explicitly referenced,
        // failing to read it must NOT silently fall back to the SA password — the DMK would be
        // created with the wrong password and the mismatch only surfaces much later.
        string masterKeyPassword;
        if (entity.Spec.MasterKeyPasswordSecretRef is { } mkRef)
        {
            masterKeyPassword = await ResolveSecretAsync(mkRef, ns, ct)
                ?? throw new InvalidOperationException(
                    $"masterKeyPasswordSecretRef '{mkRef.Name}' (key '{mkRef.Key}') not found in namespace '{ns}'; refusing to fall back to the SA password.");
        }
        else
        {
            masterKeyPassword = saPassword; // documented default when no reference is configured
        }

        // 1b. Backup SMK on each replica
        await BackupSmkForAgAsync(entity, saPassword, ns, name, replicaCount, headlessService, ct);

        // 2. PerReplica certificate model: each replica creates its own cert, then cross-import public keys
        var certPublicKeys = new Dictionary<string, byte[]>(); // podName -> public cert bytes
        var hadrSpec = entity.Spec.HadrCertificate;

        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var podFqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";
            var certName = $"{CertNamePrefix}{podName}";

            await using var conn = await connectionFactory.CreateConnectionAsync(podFqdn, 1433, saPassword, "master", ct);

            // Master Key
            if (!await agManager.MasterKeyExistsAsync(conn, ct))
                await agManager.CreateMasterKeyAsync(conn, masterKeyPassword, ct);

            // Create per-replica certificate
            if (!await agManager.CertificateExistsAsync(conn, certName, ct))
            {
                var subject = hadrSpec?.Generate?.Subject ?? $"HADR Certificate for {podName}";
                await agManager.CreateHadrCertificateAsync(conn, certName, subject, ct);
                logger.LogInformation("Created per-replica certificate {Cert} on {Pod}", certName, podName);
            }

            // Export public key bytes
            var publicCert = await agManager.ExportHadrPublicCertificateAsync(conn, certName, ct);
            certPublicKeys[podName] = publicCert;

            // Create endpoint using this replica's certificate
            if (!await agManager.EndpointExistsAsync(conn, endpointSpec.Name, ct))
            {
                await agManager.CreateEndpointAsync(conn, endpointSpec.Name, endpointSpec.Port, certName, endpointSpec.EncryptionAlgorithm, ct);
                logger.LogInformation("Created HADR endpoint on {Pod}", podName);
            }
        }

        // 3. Cross-import public certificates and create logins for endpoint connect
        for (var i = 0; i < replicaCount; i++)
        {
            var localPod = $"{name}-{i}";
            var localFqdn = $"{localPod}.{headlessService}.{ns}.svc.cluster.local";

            await using var conn = await connectionFactory.CreateConnectionAsync(localFqdn, 1433, saPassword, "master", ct);

            for (var j = 0; j < replicaCount; j++)
            {
                if (i == j) continue; // Skip self

                var remotePod = $"{name}-{j}";
                var remoteCertName = $"{CertNamePrefix}{remotePod}";
                var loginName = $"{LoginPrefix}{remotePod}_login";

                // Import remote replica's public certificate
                if (!await agManager.CertificateExistsAsync(conn, remoteCertName, ct))
                {
                    await agManager.ImportTrustedReplicaCertificateAsync(conn, remoteCertName, certPublicKeys[remotePod], ct);
                    logger.LogInformation("Imported public cert {Cert} on {Local} (from {Remote})",
                        remoteCertName, localPod, remotePod);
                }

                // Create login from certificate and grant endpoint connect
                if (!await agManager.LoginExistsAsync(conn, loginName, ct))
                {
                    await agManager.CreateEndpointLoginFromCertificateAsync(conn, loginName, remoteCertName, ct);
                    await agManager.GrantEndpointConnectAsync(conn, endpointSpec.Name, loginName, ct);
                    logger.LogInformation("Created login {Login} and granted endpoint connect on {Pod}",
                        loginName, localPod);
                }
            }
        }

        // 4. Create Availability Group on primary
        var primaryFqdn = $"{name}-0.{headlessService}.{ns}.svc.cluster.local";
        await using (var primaryConn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct))
        {
            if (!await agManager.AvailabilityGroupExistsAsync(primaryConn, name, ct))
            {
                var topology = BuildTopology(entity, headlessService, ns, endpointSpec.Port, isStandard);
                await agManager.CreateAvailabilityGroupAsync(primaryConn, topology, ct);
                logger.LogInformation("Created Availability Group {Ag} with CLUSTER_TYPE=NONE", name);
                await eventPublisher.PublishAsync(entity, "AgCreated", $"Availability Group {name} created ({entity.Spec.Edition})", ct: ct);
            }
        }

        // 5. Join secondaries
        for (var i = 1; i < replicaCount; i++)
        {
            var secondaryFqdn = $"{name}-{i}.{headlessService}.{ns}.svc.cluster.local";
            await using var secondaryConn = await connectionFactory.CreateConnectionAsync(secondaryFqdn, 1433, saPassword, "master", ct);

            if (await agManager.GetLocalReplicaRoleAsync(secondaryConn, name, ct) == null)
            {
                await agManager.JoinAvailabilityGroupAsync(secondaryConn, name, ct);
                await agManager.GrantCreateAnyDatabaseAsync(secondaryConn, name, ct);
                logger.LogInformation("Secondary {Pod} joined AG {Ag}", $"{name}-{i}", name);
                await eventPublisher.PublishAsync(entity, "ReplicaJoined", $"Replica {name}-{i} joined AG", ct: ct);
            }
        }

        // 6. Configure AG settings
        await using (var primaryConn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct))
        {
            await agManager.SetRequiredSyncSecondariesAsync(primaryConn, name,
                entity.Spec.Replicas.RequiredSynchronizedSecondariesToCommit, ct);

            if (!isStandard)
            {
                if (entity.Spec.BackupPreference != null)
                    await agManager.SetBackupPreferenceAsync(primaryConn, name, entity.Spec.BackupPreference, ct);

                if (entity.Spec.ReadOnlyRouting?.Enabled == true)
                {
                    var secondaryNames = Enumerable.Range(1, replicaCount - 1).Select(i => $"{name}-{i}").ToList();
                    await agManager.ConfigureReadOnlyRoutingAsync(primaryConn, name, $"{name}-0", secondaryNames,
                        headlessService, ns, ct);
                }
            }
        }

        // 7. Label pods with roles
        await agPodManager.SetPodRoleLabelAsync($"{name}-0", ns, name, "primary", ct);
        for (var i = 1; i < replicaCount; i++)
            await agPodManager.SetPodRoleLabelAsync($"{name}-{i}", ns, name, "secondary", ct);

        await eventPublisher.PublishAsync(entity, "AgInitialized", $"AG {name} fully initialized with {replicaCount} replicas (PerReplica cert model)", ct: ct);

        // Apply memory alignment on all replicas if memory spec is defined
        var tmpl = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (tmpl.Memory != null)
        {
            entity.Status ??= new SqlAvailabilityGroupStatus();
            // Initializing: every pod was just confirmed Ready, so all replicas are reachable.
            entity.Status.Memory = await ApplyMemoryAlignmentOnReplicasAsync(entity, tmpl.Memory, saPassword, null, ct);
        }

        // Update status
        entity.Status ??= new SqlAvailabilityGroupStatus();
        entity.Status.Edition = entity.Spec.Edition;
        entity.Status.ClusterType = "None";
        entity.Status.PrimaryReplica = $"{name}-0";
        entity.Status.PrimaryEndpoint = entity.Spec.Listener != null
            ? $"{name}-listener.{ns}.svc.cluster.local:{entity.Spec.Listener.Port}"
            : null;
        entity.Status.ReadOnlyEndpoint = !isStandard && entity.Spec.ReadOnlyListener != null
            ? $"{name}-ro.{ns}.svc.cluster.local:{entity.Spec.ReadOnlyListener.Port}"
            : null;

        // Build certificate status list
        entity.Status.Certificates = [];
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var trustedBy = Enumerable.Range(0, replicaCount).Where(j => j != i).Select(j => $"{name}-{j}").ToList();
            entity.Status.Certificates.Add(new AgCertificateStatus
            {
                ReplicaName = podName,
                Name = $"{CertNamePrefix}{podName}",
                TrustedByReplicas = trustedBy
            });
        }

        return await UpdatePhase(entity, "Running", "AgHealthy", $"AG running with {replicaCount} replicas", ct);
    }

    // ── Phase: Running / Degraded ─────────────────────────

    private async Task<SqlAvailabilityGroup> ReconcileRunningAsync(SqlAvailabilityGroup entity, string saPassword, bool isStandard, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";

        // Keep re-applying the owned infrastructure while Running — builders are
        // idempotent, the config hash drives rolling restarts, and this is what lets image or
        // configuration changes reach the StatefulSet after initial provisioning.
        try
        {
            await ApplyAgInfrastructureAsync(entity, isStandard, ct);
        }
        catch (MachineKeySecretMissingException ex)
        {
            // Degraded + condition (was "Failed" phase), so health/failover keep being
            // evaluated and the AG self-heals when the machine-key secret returns.
            await eventPublisher.PublishAsync(entity, "MachineKeySecretMissing", ex.Message, "Warning", ct);
            return await MarkNotReady(entity, "MachineKeySecretMissing", ex.Message, ct);
        }

        // Health check (timed for the ag_health_check_duration_seconds histogram).
        var healthSw = System.Diagnostics.Stopwatch.StartNew();
        var health = await agHealthChecker.CheckHealthAsync(entity, saPassword, ct);
        metrics.RecordAgHealthCheckDuration(name, healthSw.Elapsed.TotalSeconds);

        // Failover orchestration: handle manual failover annotations and, when no primary is
        // reachable, automatically promote a synchronized secondary.
        var failover = await failoverOrchestrator.ReconcileFailoverAsync(entity, saPassword, health, ct);
        if (failover is FailoverOutcome.PlannedFailover or FailoverOutcome.ForcedFailover)
        {
            // Topology just changed; the orchestrator updated Status.PrimaryReplica/LastFailover.
            // Persist and re-observe on the next (short) cycle once the new primary settles.
            return await UpdatePhase(entity, "FailingOver", "FailoverExecuted",
                $"Failover to {entity.Status?.PrimaryReplica} executed; awaiting confirmation", ct);
        }

        // AG-aware rolling update: runs only when no failover happened this cycle. It
        // advances by at most one pod, gated on AG health, and persists progress in status.
        // Subsequent steps (labels, routing, REQUIRED_SYNC) still run so the AG stays consistent.
        await rollingUpdateOrchestrator.ReconcileAsync(entity, health, saPassword, ct);

        // Update pod labels: role, sync, and the read-routing labels (readable / read-intent-ready /
        // listener-ready) that drive the listener selectors.
        //
        // When no primary answers, the health report falls back to the view of a
        // SECONDARY, whose DMVs only know their own role — every other replica reads UNKNOWN. Running
        // the loop on that view demoted the real primary's labels (UNKNOWN → "secondary",
        // listener-ready=none) and drained the R/W listener on a single 3s probe miss, bypassing
        // PrimaryUnreachableThreshold entirely. The deliberate drain already exists: the failover
        // orchestrator calls MarkAllResolving when the threshold is genuinely reached.
        if (health.PrimaryReplica != null)
        {
            foreach (var replica in health.ReplicaStates)
            {
                // A replica whose role the report cannot determine must not be relabelled from a guess.
                if (replica.Role.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isPrimary = replica.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
                var role = isPrimary ? "primary" : "secondary";
                await agPodManager.SetPodRoleLabelAsync(replica.Name, ns, name, role, ct);
                await agPodManager.SetPodSyncLabelsAsync(replica.Name, ns, replica.SynchronizationHealth, replica.IsFailoverReady, ct);

                var (readable, readIntentReady, listenerReady) = ComputeReadRouting(replica, isPrimary);
                await agPodManager.SetReadabilityLabelsAsync(replica.Name, ns, readable, readIntentReady, listenerReady, ct);
            }
        }
        else
        {
            logger.LogWarning("AG {Name}: no primary in this health pass — keeping the current pod labels " +
                              "(the failover orchestrator drains the writer path once the unreachable threshold is reached)", name);
        }

        // Update status replicas. Convergence state only — replication lag and
        // freshness are live telemetry, served by agreplicastates (view API) and metrics.
        entity.Status ??= new SqlAvailabilityGroupStatus();
        entity.Status.PrimaryReplica = health.PrimaryReplica;
        entity.Status.ClusterType = "None";
        entity.Status.Replicas = health.ReplicaStates.Select(r =>
        {
            var isPrimary = r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
            var (readable, readIntentReady, listenerReady) = ComputeReadRouting(r, isPrimary);
            return new AgReplicaStatus
            {
                Name = r.Name,
                Role = r.Role,
                AvailabilityMode = r.AvailabilityMode is "SYNCHRONOUS_COMMIT" ? "SynchronousCommit"
                    : r.AvailabilityMode is "ASYNCHRONOUS_COMMIT" ? "AsynchronousCommit"
                    : (isPrimary ? "SynchronousCommit" : "Unknown"),
                SynchronizationState = r.SynchronizationHealth,
                ConnectionState = r.ConnectionState,
                IsFailoverReady = r.IsFailoverReady,
                Readable = readable,
                ListenerReady = listenerReady
            };
        }).ToList();

        // Aggregate replication lag per replica from the per-database DMV figures.
        // Lag is telemetry — it feeds the Prometheus gauges below, never the status.
        var lagByReplica = health.DatabaseStates
            .GroupBy(d => d.ReplicaName)
            .ToDictionary(
                g => g.Key,
                g => (LogSend: g.Sum(d => d.LogSendQueueSize), Redo: g.Sum(d => d.RedoQueueSize)));

        // Publish AG/replica gauges. Queue sizes are reported in bytes (KB×1024).
        metrics.SetAgReplicaGauges(name,
            total: health.ReplicaStates.Count,
            synchronized: health.ReplicaStates.Count(r => r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)),
            disconnected: health.ReplicaStates.Count(r => r.ConnectionState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase)));
        foreach (var (replica, lag) in lagByReplica)
            metrics.SetReplicaLag(name, replica, lag.LogSend * 1024, lag.Redo * 1024);

        // While we hold a primary connection, reconcile read-only routing against the live topology
        // and keep REQUIRED_SYNC safe under degradation.
        if (health.PrimaryReplica != null)
        {
            var primaryFqdn = $"{health.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";
            try
            {
                await using var primaryConn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct);
                if (!isStandard && entity.Spec.ReadOnlyRouting?.Enabled == true)
                    await ReconcileReadOnlyRoutingAsync(entity, primaryConn, health, headlessService, ns, ct);
                await AdjustRequiredSyncAsync(entity, primaryConn, health, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not reconcile routing/required-sync for AG {Name}", name);
            }

            // Converge replicas that lost AG membership (e.g. recreated on a blank PV) by
            // re-joining them where their crypto survived.
            if (health.ReplicasMissingMembership.Count > 0)
                await ConvergeMissingReplicasAsync(entity, saPassword, health, headlessService, ns, ct);
        }

        // Aggregate database summary from SqlAvailabilityGroupDatabase CRDs
        entity.Status.DatabaseSummary = await BuildDatabaseSummaryAsync(name, ns, ct);

        // Memory alignment on running replicas — only on replicas the health pass just reached
        // opening a full-profile connection (30s × 5 retries ≈ 2.6 min) to a replica
        // whose node is down held a worker hostage on every single pass, during the outage itself.
        var tmplRunning = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (tmplRunning.Memory != null)
        {
            entity.Status.Memory = await ApplyMemoryAlignmentOnReplicasAsync(entity, tmplRunning.Memory, saPassword, health.ReachableReplicas, ct);
        }

        // Emit warnings
        foreach (var warning in health.Warnings)
        {
            if (warning.Contains("SPLIT-BRAIN"))
                await eventPublisher.PublishAsync(entity, "SplitBrainDetected", warning, "Warning", ct);
            else if (warning.Contains("DISCONNECTED"))
                await eventPublisher.PublishAsync(entity, "ReplicaDisconnected", warning, "Warning", ct);
        }

        // Data movement resume is now handled by SqlAvailabilityGroupDatabaseController

        // Determine phase
        var newPhase = health.IsHealthy ? "Running" : "Degraded";
        var reason = health.IsHealthy ? "AgHealthy" : "AgDegraded";
        var message = health.IsHealthy
            ? $"AG running with {health.ReplicaStates.Count} replicas"
            : $"AG degraded: {string.Join("; ", health.Warnings.Take(3))}";

        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Edition = entity.Spec.Edition;

        return await UpdatePhase(entity, newPhase, reason, message, ct);
    }

    public Task<ReconciliationResult<SqlAvailabilityGroup>> DeletedAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAvailabilityGroup {Name} deleted", entity.Metadata.Name);
        metrics.ForgetAg(entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroup>.Success(entity));
    }

    // ── Read-routing & quorum helpers ─────────────────────

    /// <summary>
    /// Derive the read-routing labels for a replica: a secondary is <c>readable</c> when it is
    /// connected and its <c>ALLOW_CONNECTIONS</c> is not <c>NO</c>; it is <c>read-intent-ready</c>
    /// only when also synchronized. The primary is the writer; everything else is <c>none</c>.
    /// </summary>
    private static (bool Readable, bool ReadIntentReady, string ListenerReady) ComputeReadRouting(
        Services.SqlServer.AgReplicaStateInfo r, bool isPrimary)
        => Services.SqlServer.AgReadRouting.Compute(r, isPrimary);

    /// <summary>
    /// Re-apply read-only routing every cycle so it tracks the live primary and only lists
    /// secondaries that are connected and readable. The Initializing-time
    /// configuration is static and breaks after the first failover; this keeps it correct.
    /// </summary>
    private async Task ReconcileReadOnlyRoutingAsync(
        SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        Services.Health.AgHealthReport health, string headlessService, string ns, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var primary = health.PrimaryReplica!;

        var readableSecondaries = health.ReplicaStates
            .Where(r => !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
                        && !r.AllowConnections.Equals("NO", StringComparison.OrdinalIgnoreCase)
                        && !r.AllowConnections.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        if (readableSecondaries.Count == 0)
            return;

        // Only re-apply when the routing topology actually changed. The signature captures a
        // failover (primary changes), a replica add/remove or a readability flip (set changes) — the
        // exact transitions that require reconfiguration — avoiding redundant ALTERs every cycle.
        // Routing URLs address POD FQDNs, so the port is always the container port —
        // the listener Service port has no meaning there and produced dead routing URLs.
        var signature = $"{primary}|{string.Join(",", readableSecondaries)}|{SqlEndpoints.ContainerPort}";
        if (entity.Status?.ReadOnlyRoutingSignature == signature)
            return;

        await agManager.ConfigureReadOnlyRoutingAsync(primaryConn, name, primary, readableSecondaries,
            headlessService, ns, ct);
        entity.Status!.ReadOnlyRoutingSignature = signature;
    }

    /// <summary>
    /// Keep <c>REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT</c> at the lesser of the configured value
    /// and the number of currently connected synchronous secondaries. Without this, losing
    /// the only synchronous secondary freezes all commits on the primary. The value is restored as
    /// soon as the secondary reconnects. Only issues an ALTER when the value actually changes.
    /// </summary>
    private async Task AdjustRequiredSyncAsync(
        SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        Services.Health.AgHealthReport health, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var configured = entity.Spec.Replicas.RequiredSynchronizedSecondariesToCommit;

        var connectedSyncSecondaries = health.ReplicaStates.Count(r =>
            !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            && r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase)
            && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
            && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));

        var desired = Math.Max(0, Math.Min(configured, connectedSyncSecondaries));
        var current = await agManager.GetRequiredSyncSecondariesAsync(primaryConn, name, ct);
        if (current == desired)
            return;

        await agManager.SetRequiredSyncSecondariesAsync(primaryConn, name, desired, ct);
        if (desired < configured)
            await eventPublisher.PublishAsync(entity, "RequiredSyncLowered",
                $"Lowered REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT from {configured} to {desired} " +
                "to keep writes available while a synchronous secondary is unavailable", "Warning", ct);
        else
            await eventPublisher.PublishAsync(entity, "RequiredSyncRestored",
                $"Restored REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT to {desired}", ct: ct);
    }

    // ── Helpers ───────────────────────────────────────────

    /// <summary>
    /// Re-join replicas that no longer know the AG. Safe when their endpoint/cert survived a restart
    /// (the common case): a single idempotent JOIN brings them back. When the crypto is also gone
    /// (truly blank PV), JOIN fails its endpoint auth — we surface an actionable event rather than
    /// silently leaving the AG under-replicated.
    /// </summary>
    private async Task ConvergeMissingReplicasAsync(
        SqlAvailabilityGroup entity, string saPassword, Services.Health.AgHealthReport health,
        string headlessService, string ns, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var endpointName = (entity.Spec.Endpoint ?? new AgEndpointSpec()).Name;

        foreach (var replica in health.ReplicasMissingMembership)
        {
            var fqdn = $"{replica}.{headlessService}.{ns}.svc.cluster.local";
            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(fqdn, 1433, saPassword, "master", ct);

                // Endpoint present → crypto survived → a JOIN is enough to restore membership.
                if (await agManager.EndpointExistsAsync(conn, endpointName, ct))
                {
                    await agManager.JoinAvailabilityGroupAsync(conn, name, ct);
                    await agManager.GrantCreateAnyDatabaseAsync(conn, name, ct);
                    await eventPublisher.PublishAsync(entity, "ReplicaRejoined",
                        $"Replica {replica} lost AG membership and was automatically re-joined", "Warning", ct);
                    logger.LogWarning("Re-joined replica {Replica} to AG {Ag}", replica, name);
                }
                else
                {
                    await eventPublisher.PublishAsync(entity, "ReplicaNeedsReprovision",
                        $"Replica {replica} lost AG membership and its HADR endpoint/crypto (likely a blank PV). " +
                        "Re-provisioning the certificate chain requires recreating the AG crypto for this replica.",
                        "Warning", ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Convergence re-join of replica {Replica} failed", replica);
            }
        }
    }

    private async Task<MemoryStatus> ApplyMemoryAlignmentOnReplicasAsync(
        SqlAvailabilityGroup entity, MemorySpec memSpec, string saPassword,
        IReadOnlyCollection<string>? reachableReplicas, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var replicaCount = entity.Spec.Replicas.Count;

        var totalMb = MemoryCalculator.ParseToMb(memSpec.Total);
        var reserveMb = MemoryCalculator.ParseToMb(memSpec.SystemReserve);
        // Same runtimeClassName-aware calculation as the env var — diverging values
        // under Kata made the Aligned status oscillate.
        var sqlMaxMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memSpec, (entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec()).RuntimeClassName);
        var sqlMinMb = MemoryCalculator.CalculateSqlMinMemoryMb(memSpec);
        var allAligned = true;

        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var podFqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";

            // Skip replicas the health pass could not reach — their 3s probe already
            // failed, so a 2.6-minute full-profile connect attempt can only waste the worker.
            if (reachableReplicas != null && !reachableReplicas.Contains(podName))
            {
                logger.LogDebug("Skipping memory alignment on unreachable replica {Pod}", podName);
                allAligned = false;
                continue;
            }

            try
            {
                await using var conn = await connectionFactory.CreateProbeConnectionAsync(podFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                var currentConfig = await sqlServerManager.GetCurrentConfigAsync(conn, ct);
                var currentMax = currentConfig.GetValueOrDefault("max server memory (MB)", 0);
                var currentMin = currentConfig.GetValueOrDefault("min server memory (MB)", 0);

                if (currentMax != sqlMaxMb || (sqlMinMb > 0 && currentMin != sqlMinMb))
                {
                    if (currentMax != sqlMaxMb)
                        await sqlServerManager.ApplySpConfigureAsync(conn, "max server memory (MB)", sqlMaxMb, ct);
                    if (sqlMinMb > 0 && currentMin != sqlMinMb)
                        await sqlServerManager.ApplySpConfigureAsync(conn, "min server memory (MB)", sqlMinMb, ct);
                    await sqlServerManager.ReconfigureAsync(conn, ct: ct);
                    logger.LogInformation("Applied memory alignment on {Pod}: max={MaxMb} MB, min={MinMb} MB", podName, sqlMaxMb, sqlMinMb);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply memory alignment on replica {Pod}", podName);
                allAligned = false;
            }
        }

        if (allAligned)
        {
            await eventPublisher.PublishAsync(entity, "MemoryConfigured",
                $"Memory aligned on all {replicaCount} replicas: max server memory = {sqlMaxMb} MB (pod limit: {totalMb} MB, reserve: {reserveMb} MB)", ct: ct);
        }

        return new MemoryStatus
        {
            PodLimitMb = totalMb,
            SqlMaxMemoryMb = sqlMaxMb,
            SqlMinMemoryMb = sqlMinMb,
            SystemReserveMb = reserveMb,
            Aligned = allAligned
        };
    }

    private async Task<AgDatabaseSummaryStatus> BuildDatabaseSummaryAsync(string agName, string ns, CancellationToken ct)
    {
        var summary = new AgDatabaseSummaryStatus();
        try
        {
            var sagdbs = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct);
            var matching = sagdbs.Where(s => s.Spec.AvailabilityGroupRef.Name == agName).ToList();

            summary.Total = matching.Count;
            summary.Synchronized = matching.Count(s => s.Status?.Phase == "Synchronized");
            summary.Synchronizing = matching.Count(s => s.Status?.Phase is "Synchronizing" or "Seeding" or "Adding");
            // "NotSynchronizing" is a real phase of SqlAvailabilityGroupDatabase but was
            // in no bucket — a database that had stopped replicating simply vanished from the roll-up.
            summary.Suspended = matching.Count(s => s.Status?.Phase is "Suspended" or "NotSynchronizing");
            summary.Failed = matching.Count(s => s.Status?.Phase == "Failed");
            // Per-database entries removed: the counts above are the O(1) roll-up; per-database
            // phase is served live by the aggregated APIService and by each SqlAvailabilityGroupDatabase.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build database summary for AG {Ag}", agName);
        }
        return summary;
    }

    private AgTopologyConfig BuildTopology(SqlAvailabilityGroup entity, string headlessService, string ns, int endpointPort, bool isStandard)
    {
        var name = entity.Metadata.Name;
        var replicaCount = entity.Spec.Replicas.Count;
        var syncCount = entity.Spec.Replicas.SynchronousCount;
        var defaults = entity.Spec.ReplicaDefaults ?? new AgReplicaDefaultsSpec();

        // Standard: all must be SYNCHRONOUS_COMMIT, ALLOW_CONNECTIONS = NO
        var replicas = new List<AgReplicaConfig>();
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var endpointUrl = $"tcp://{podName}.{headlessService}.{ns}.svc.cluster.local:{endpointPort}";

            // Check for per-replica override
            var ov = entity.Spec.Replicas.Overrides?.FirstOrDefault(o => o.Ordinal == i);

            string availabilityMode;
            if (isStandard)
            {
                availabilityMode = "SYNCHRONOUS_COMMIT"; // Standard: always sync
            }
            else
            {
                availabilityMode = ov?.AvailabilityMode switch
                {
                    "AsynchronousCommit" => "ASYNCHRONOUS_COMMIT",
                    "SynchronousCommit" => "SYNCHRONOUS_COMMIT",
                    _ => i < syncCount ? "SYNCHRONOUS_COMMIT" : "ASYNCHRONOUS_COMMIT"
                };
            }

            string allowConnections;
            if (isStandard)
            {
                allowConnections = "NO"; // Standard: no readable secondaries
            }
            else
            {
                var readable = ov?.ReadableSecondary ?? defaults.ReadableSecondary;
                allowConnections = readable switch
                {
                    "No" => "NO",
                    "ReadOnly" => "READ_ONLY",
                    "All" => "ALL",
                    _ => "ALL"
                };
            }

            var seedingMode = defaults.SeedingMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
                ? "AUTOMATIC" : "MANUAL";

            int? backupPriority = isStandard ? null : (ov?.BackupPriority ?? defaults.BackupPriority);
            int? sessionTimeout = ov?.SessionTimeout ?? defaults.SessionTimeout;

            replicas.Add(new AgReplicaConfig(
                Name: podName,
                EndpointUrl: endpointUrl,
                AvailabilityMode: availabilityMode,
                FailoverMode: "MANUAL",
                SeedingMode: seedingMode,
                AllowConnections: allowConnections,
                BackupPriority: backupPriority,
                SessionTimeout: sessionTimeout
            ));
        }

        return new AgTopologyConfig(name, headlessService, ns, endpointPort, entity.Spec.Edition, replicas);
    }

    private async Task<SqlAvailabilityGroup> UpdatePhase(SqlAvailabilityGroup entity, string phase, string reason, string message, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;

        var splitBrainSafe = !message.Contains("SPLIT-BRAIN", StringComparison.OrdinalIgnoreCase)
                             && !reason.Contains("SplitBrain", StringComparison.OrdinalIgnoreCase);

        // Upsert through ConditionOps on the existing list — replacing the whole
        // array dropped conditions set elsewhere and reset their transition timestamps.
        entity.Status.Conditions ??= [];
        ConditionOps.Set(entity.Status.Conditions, "Ready", phase == "Running" ? "True" : "False", reason, message);
        ConditionOps.Set(entity.Status.Conditions, "SplitBrainSafe", splitBrainSafe ? "True" : "False",
            splitBrainSafe ? "NoSplitBrain" : "SplitBrainDetected",
            splitBrainSafe ? "No split-brain detected" : message);
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private static Dictionary<string, string> BuildAgLabels(string agName) => new()
    {
        { "mssql-operator.evariops.com/ag", agName }
    };

    private static V1OwnerReference BuildOwnerRef(SqlAvailabilityGroup entity) => new()
    {
        ApiVersion = entity.ApiVersion,
        Kind = entity.Kind,
        Name = entity.Metadata.Name,
        Uid = entity.Metadata.Uid,
        Controller = true,
        BlockOwnerDeletion = true
    };

    private async Task ApplyConfigMapAsync(string name, string ns, string confContent, SqlAvailabilityGroup owner, CancellationToken ct)
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
                    [OperatorLabels.Ag] = name,
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

        // Server-side apply.
        await kubernetesClient.ApplyAsync(cm, ct);
    }

    private async Task ApplyPodDisruptionBudgetAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;
        var pdb = new V1PodDisruptionBudget
        {
            ApiVersion = "policy/v1",
            Kind = "PodDisruptionBudget",
            Metadata = new V1ObjectMeta
            {
                Name = $"{name}-pdb",
                NamespaceProperty = ns,
                // managed-by on metadata only; the PDB selector is immutable.
                Labels = new Dictionary<string, string>(BuildAgLabels(name)) { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                OwnerReferences = [BuildOwnerRef(entity)]
            },
            Spec = new V1PodDisruptionBudgetSpec
            {
                MaxUnavailable = 1,
                Selector = new V1LabelSelector { MatchLabels = BuildAgLabels(name) }
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
            logger.LogWarning(ex, "Failed to apply PodDisruptionBudget for AG {Name}", name);
        }
    }

    private async Task CheckStorageReclaimPolicyAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        var storage = (entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec()).Storage;
        if (storage == null) return;

        var classNames = new[] { storage.Data, storage.SysDb, storage.Log, storage.Backup, storage.CertBackups }
            .Select(v => v?.StorageClassName)
            .Where(n => n != null)
            .Distinct()
            .ToList();

        foreach (var storageClassName in classNames)
        {
            try
            {
                var sc = await kubernetesClient.GetAsync<V1StorageClass>(storageClassName!, null, ct);
                if (sc != null && !string.Equals(sc.ReclaimPolicy, "Retain", StringComparison.OrdinalIgnoreCase))
                {
                    await eventPublisher.PublishAsync(entity, "StorageReclaimPolicyUnsafe",
                        $"StorageClass '{storageClassName}' has reclaimPolicy '{sc.ReclaimPolicy}': deleting a PVC will destroy AG data. Use reclaimPolicy=Retain for AG storage.",
                        "Warning", ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check reclaimPolicy of StorageClass {Sc}", storageClassName);
            }
        }
    }

    private async Task ApplyStatefulSetAsync(V1StatefulSet sts, CancellationToken ct)
    {
        // Server-side apply — no 422 loop on immutable volumeClaimTemplates, no
        // clobbering of fields owned by other actors (webhooks, sidecar injectors).
        await kubernetesClient.ApplyAsync(sts, ct);
    }

    private async Task ApplyServiceAsync(V1Service svc, CancellationToken ct)
    {
        // SSA preserves clusterIP and third-party fields automatically.
        await kubernetesClient.ApplyAsync(svc, ct);
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct) =>
        await ResolveSecretAsync(entity.Spec.SaPasswordSecretRef, ns, ct);

    private async Task<string?> ResolveSecretAsync(SecretReference? secretRef, string ns, CancellationToken ct)
    {
        if (secretRef == null) return null;
        // Null means "absent" (GetAsync already maps 404 to null); transport and
        // auth errors propagate so the runtime backs off instead of acting on a wrong value.
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
        if (secret?.Data == null) return null;
        return secret.Data.TryGetValue(secretRef.Key, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    private async Task BackupSmkForAgAsync(SqlAvailabilityGroup entity, string saPassword, string ns, string name, int replicaCount, string headlessService, CancellationToken ct)
    {
        try
        {
            // Resolve or generate SMK backup password
            var smkPassword = await ResolveSmkPasswordForAgAsync(entity, ns, ct);
            if (smkPassword == null) return;

            for (var i = 0; i < replicaCount; i++)
            {
                var podName = $"{name}-{i}";
                var smkSecretName = $"{podName}-smk-backup";

                // Skip if already backed up
                var existing = await kubernetesClient.GetAsync<V1Secret>(smkSecretName, ns, ct);
                if (existing?.Data != null && existing.Data.ContainsKey("smk.bak"))
                    continue;

                var podFqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";
                await using var conn = await connectionFactory.CreateConnectionAsync(podFqdn, 1433, saPassword, "master", ct);

                // Unguessable, shape-validated temp path: same hardened pattern as
                // SqlInstanceController.ReadFileViaSqlAsync — EnsureSafe + QuoteStringLiteral, never
                // raw interpolation, even though the path is operator-generated.
                var backupPath = SqlTempPaths.Create("bak");
                await smkManager.BackupSmkAsync(conn, backupPath, smkPassword, ct);

                // Read the backup via SQL
                await using var readCmd = conn.CreateCommand();
                SqlTempPaths.EnsureSafe(backupPath);
                readCmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(backupPath)}, SINGLE_BLOB) AS x";
                var smkBytes = await readCmd.ExecuteScalarAsync(ct) as byte[];

                if (smkBytes == null || smkBytes.Length == 0)
                {
                    logger.LogWarning("SMK backup file is empty for {Pod}", podName);
                    continue;
                }

                var ownerRef = BuildOwnerRef(entity);
                var smkSecret = new V1Secret
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = smkSecretName,
                        NamespaceProperty = ns,
                        // Managed-by for inventory (Secrets are excluded from the owned-resource watch).
                        Labels = new Dictionary<string, string> { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                        OwnerReferences = [ownerRef],
                        Annotations = new Dictionary<string, string>
                        {
                            ["argocd.argoproj.io/sync-options"] = "Delete=false"
                        }
                    },
                    Type = "Opaque",
                    Data = new Dictionary<string, byte[]>
                    {
                        ["smk.bak"] = smkBytes
                    }
                };

                await kubernetesClient.CreateAsync(smkSecret, ct);
                logger.LogInformation("Created SMK backup secret {Secret} for {Pod}", smkSecretName, podName);

                // The temp .bak stays in the pod's ephemeral /tmp under a unique name: SQL Server on
                // Linux has no native command to delete an arbitrary file (xp_cmdshell is unavailable),
                // so cleanup is left to the pod lifecycle.
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to backup SMK for AG {Name} — will retry", name);
        }
    }

    private async Task<string?> ResolveSmkPasswordForAgAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SmkBackupPasswordSecretRef != null)
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SmkBackupPasswordSecretRef.Name, ns, ct);
            if (secret?.Data != null && secret.Data.TryGetValue(entity.Spec.SmkBackupPasswordSecretRef.Key, out var bytes))
                return System.Text.Encoding.UTF8.GetString(bytes);
        }

        var passwordSecretName = $"{entity.Metadata.Name}-smk-backup-password";
        var existing = await kubernetesClient.GetAsync<V1Secret>(passwordSecretName, ns, ct);
        if (existing?.Data != null && existing.Data.TryGetValue("password", out var existingBytes))
            return System.Text.Encoding.UTF8.GetString(existingBytes);

        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var ownerRef = BuildOwnerRef(entity);

        var newSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = passwordSecretName,
                NamespaceProperty = ns,
                // Managed-by for inventory (Secrets are excluded from the owned-resource watch).
                Labels = new Dictionary<string, string> { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                OwnerReferences = [ownerRef],
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false"
                }
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password)
            }
        };

        await kubernetesClient.CreateAsync(newSecret, ct);
        logger.LogInformation("Generated SMK backup password secret {Secret} for AG", passwordSecretName);
        return password;
    }
}
