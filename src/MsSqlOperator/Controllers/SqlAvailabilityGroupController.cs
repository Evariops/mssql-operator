using MsSqlOperator.Services.Actuation;
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
    ISaPasswordConverger saPasswordConverger,
    IRolloutBudget rolloutBudget,
    IVolumeAutoGrower volumeAutoGrower,
    Operator.Registration.OperatorOptions options,
    Services.Metrics.OperatorMetrics metrics,
    TimeProvider timeProvider,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null,
    Services.Control.ControlRegistry? controlRegistry = null) : IEntityController<SqlAvailabilityGroup>
{
    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    private const string CertNamePrefix = "HadrCert_";
    private const string LoginPrefix = "hadr_";

    public async Task<ReconciliationResult<SqlAvailabilityGroup>> ReconcileAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlAvailabilityGroup {Name} (phase={Phase})", entity.Metadata.Name, entity.Status?.Phase ?? "null");

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var edition = entity.Spec.Edition;
        var isStandard = edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);

        // Refuse to touch children that belong to a resource of another kind.
        try
        {
            return await ReconcileGuardedAsync(entity, ct);
        }
        catch (ForeignChildException ex)
        {
            logger.LogError(ex, "SqlAvailabilityGroup {Name}: refusing to reconcile — {Message}", name, ex.Message);
            await eventPublisher.PublishAsync(entity, "ForeignChildResource", ex.Message, "Warning", ct);
            entity = await MarkNotReady(entity, "ForeignChildResource", ex.Message, ct);
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromMinutes(5));
        }
    }

    private async Task<ReconciliationResult<SqlAvailabilityGroup>> ReconcileGuardedAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
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
        if (currentPhase is Phases.Initializing or Phases.Pending)
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, TimeSpan.FromSeconds(15));

        // Steady-state phases must be re-reconciled periodically: a primary that dies
        // without touching the CR would otherwise never be re-detected (no event watch fires).
        if (currentPhase is Phases.Running or Phases.Degraded or Phases.FailingOver)
        {
            // Right after a failover, the previous primary may come back as PRIMARY any second.
            if (WatchedResurrection(entity) is not null)
                return ReconciliationResult<SqlAvailabilityGroup>.Success(entity, ResurrectionWatchInterval);
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
        return await UpdatePhase(entity, Phases.Initializing, "InfrastructureReady", $"StatefulSet, services created. Waiting for {replicaCount} pods.", ct);
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

        var tmpl = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();

        // The image must come from an allowed registry, and the security contexts stay within
        // the policy (defence in depth behind admission).
        if ((ImagePolicy.Refusal(tmpl.Image, options.AllowedImageRegistries, "instanceTemplate")
             ?? SecurityContextPolicy.Check(tmpl.PodSecurityContext, tmpl.SecurityContext, "instanceTemplate")) is { } refused)
            throw new InvalidOperationException(refused);

        // 1. The configuration that provides mssql.conf: configurationRef, or the single
        //    SqlConfiguration selecting this AG by labels.
        var (sqlConfig, ambiguity) = await SqlConfigurationResolver.ResolveAsync(
            kubernetesClient, ns, tmpl.ConfigurationRef, entity.Metadata.Labels, ct);
        entity.Status ??= new SqlAvailabilityGroupStatus();
        ConditionOps.Set(entity.Status.Conditions ??= [], "StartupConfigurationResolved", ambiguity is null ? "True" : "False",
            ambiguity is null ? "Resolved" : "Ambiguous",
            ambiguity ?? (sqlConfig is null ? "No SqlConfiguration applies" : $"mssql.conf from SqlConfiguration {sqlConfig.Metadata.Name}"));
        if (ambiguity is not null)
            await eventPublisher.PublishAsync(entity, "StartupConfigurationAmbiguous", ambiguity, "Warning", ct);

        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, tmpl.Memory, tmpl.Storage, tmpl.RuntimeClassName, tmpl.Tls);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);

        // 1b. Ensure machine key secret (shared across all AG replicas).
        // MachineKeySecretMissingException propagates to the caller.
        var owner = BuildOwnerRef(entity);
        var machineKey = await machineKeyManager.EnsureMachineKeySecretAsync(
            name, ns, owner,
            entity.Spec.MachineKeySecretRef?.Name, entity.Spec.MachineKeySecretRef?.Key, ct);
        var machineKeySecretName = machineKey.SecretName;
        await TrackMachineKeyAsync(entity, machineKey, ct);

        // Pods mounting a missing TLS Secret would stay in ContainerCreating.
        if (await TlsSecret.MissingAsync(kubernetesClient, tmpl.Tls, ns, ct) is { } tlsMissing)
            throw new InvalidOperationException(tlsMissing);

        // 2. Create StatefulSet
        var sts = statefulSetBuilder.BuildForAg(entity, configHash, machineKeySecretName);
        // Override to OnDelete for AG-aware rolling updates
        sts.Spec.UpdateStrategy = new V1StatefulSetUpdateStrategy { Type = "OnDelete" };
        ChildOwnershipGuard.StampOwner(sts, entity);
        await OwnedChildren.GuardExistingDataVolumesAsync(kubernetesClient, eventPublisher, logger, entity, sts, entity.Spec.Replicas.Count, ct);
        await GuardReplicaCountAsync(entity, sts, ct);

        // mssql.conf only after the StatefulSet was accepted. Written first, a ConfigMap
        // pointing masterdatafile/errorlogfile at a volume the refused StatefulSet never mounted
        // was read at the next restart (crash, or a blank master on a writable root).
        if (await ApplyStatefulSetAsync(sts, entity, ct))
            await OwnedChildren.ApplyMssqlConfAsync(kubernetesClient, entity, OperatorLabels.Ag, confContent, ct);

        // 2b. PodDisruptionBudget: never let a drain evict more than one AG replica at a time
        await OwnedChildren.ApplyPodDisruptionBudgetAsync(kubernetesClient, logger, entity, BuildAgLabels(name), maxUnavailable: 1, ct);

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
        // read-intent-ready secondary so traffic never lands on a non-readable/seeding replica
        // or the primary when none is and the listener falls back to it: the labelling
        // pass sets read-only-target accordingly.
        if (!isStandard && entity.Spec.ReadOnlyListener != null)
        {
            var roLabels = new Dictionary<string, string>(labels)
            {
                [AgPodManager.ReadOnlyTargetLabel] = "true",
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
                // Server-side apply — no GET+PUT that wrote back what others had set.
                await kubernetesClient.ApplyAsync(netpol, ct);

                // The condition was set to False on a failure and never raised back.
                entity.Status ??= new SqlAvailabilityGroupStatus();
                ConditionOps.Set(entity.Status.Conditions ??= [], "NetworkPolicyApplied", "True",
                    "Applied", $"CiliumNetworkPolicy {netpol.Metadata.Name} applied");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail-visible — never let the user believe the AG is isolated.
                logger.LogWarning(ex, "Failed to apply CiliumNetworkPolicy for AG {Name}", name);
                entity.Status ??= new SqlAvailabilityGroupStatus();
                ConditionOps.Set(entity.Status.Conditions ??= [], "NetworkPolicyApplied", "False",
                    "ApplyFailed", "The CiliumNetworkPolicy could not be applied (see events)");
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
                    return await UpdatePhase(entity, Phases.Initializing, "SchedulingBlocked",
                        $"Pod {podName} unschedulable: {unschedulable}", ct);
                }
                logger.LogInformation("Pod {Pod} not ready yet, will retry in 15s", podName);
                return await UpdatePhase(entity, Phases.Initializing, "WaitingForPods",
                    $"Pod {podName} not ready, waiting...", ct);
            }
        }

        // A pod is Ready on its own probes, which run inside it: the operator may still not reach it
        // (a network policy in a default-deny cluster). The pass then failed further down and the
        // status kept saying WaitingForPods. The condition names the pod, the event the error.
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            try
            {
                await using var probe = await connectionFactory.CreateProbeConnectionAsync(
                    SqlEndpoints.PodFqdn(name, i, headlessService, ns), SqlEndpoints.ContainerPort, saPassword, "master", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await eventPublisher.PublishAsync(entity, "ReplicaUnreachable",
                    $"Pod {podName} is Ready but the operator cannot connect to it: {ex.Message}", "Warning", ct);
                return await UpdatePhase(entity, Phases.Initializing, "ReplicaUnreachable",
                    $"Pod {podName} is Ready but the operator cannot connect to it (see events); check the network policies between the operator and the SQL pods", ct);
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
        await BackupSmkForAgAsync(entity, saPassword, ns, Enumerable.Range(0, replicaCount).Select(i => $"{name}-{i}"),
            headlessService, ct);

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
                await agManager.CreateHadrCertificateAsync(conn, certName, subject, hadrSpec?.Generate?.ExpiryDate, ct);
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

                // Create login from certificate and grant endpoint connect. The GRANT is
                // idempotent and runs on every pass — gated on the login's absence, a GRANT that
                // failed after the CREATE LOGIN was never retried and the replica never connected.
                if (!await agManager.LoginExistsAsync(conn, loginName, ct))
                {
                    await agManager.CreateEndpointLoginFromCertificateAsync(conn, loginName, remoteCertName, ct);
                    logger.LogInformation("Created login {Login} on {Pod}", loginName, localPod);
                }
                await agManager.GrantEndpointConnectAsync(conn, endpointSpec.Name, loginName, ct);
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
                logger.LogInformation("Secondary {Pod} joined AG {Ag}", $"{name}-{i}", name);
                await eventPublisher.PublishAsync(entity, "ReplicaJoined", $"Replica {name}-{i} joined AG", ct: ct);
            }

            // Idempotent, always applied — gated on the JOIN, a failed GRANT left automatic
            // seeding broken on this replica for good.
            await agManager.GrantCreateAnyDatabaseAsync(secondaryConn, name, ct);
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

        return await UpdatePhase(entity, Phases.Running, "AgHealthy", $"AG running with {replicaCount} replicas", ct);
    }

    // ── Phase: Running / Degraded ─────────────────────────

    /// <summary>
    /// When the owned infrastructure of each AG was last converged, and for which generation.
    /// The fast loop (health, failover, labels) runs every few seconds; re-applying the ConfigMap,
    /// StatefulSet, Services, PDB and network policy and re-reading PVCs and StorageClasses on every
    /// one of those passes made the cadence collapse past a few dozen AGs. In memory: after a
    /// restart, the first pass converges everything once.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Generation, DateTimeOffset At)> InfrastructureConverged = new();

    private static readonly TimeSpan InfrastructureInterval = TimeSpan.FromSeconds(60);

    private async Task<SqlAvailabilityGroup> ReconcileRunningAsync(SqlAvailabilityGroup entity, string saPassword, bool isStandard, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        entity.Status ??= new SqlAvailabilityGroupStatus();

        // The infrastructure is converged on its own, slower cadence, and a failure there no
        // longer stops the health check, the failover and the routing labels below — an SSA 422, a
        // policy webhook refusal, a quota or an API 5xx used to switch the HA off.
        var (slowPass, infrastructureError) = await ConvergeInfrastructureAsync(entity, isStandard, ct);


        // Converge the sa login of every replica before the health check, which logs in with
        // the Secret's password.
        var replicaPods = Enumerable.Range(0, entity.Spec.Replicas.Count).Select(i => $"{name}-{i}").ToList();
        var sa = await saPasswordConverger.ConvergeAsync(entity, replicaPods, saPassword, ct);
        ConditionOps.Set(entity.Status.Conditions ??= [], "SaPasswordConverged",
            sa == SaConvergence.Converged ? "True" : "False",
            sa.ToString(),
            sa switch
            {
                SaConvergence.Converged => "Every replica accepts the sa password of the referenced Secret",
                SaConvergence.Pending => "The sa password is being converged on the replicas",
                _ => "A replica refuses the sa password of the referenced Secret and the last applied one (see events)",
            });

        await ContainResurrectedPrimaryAsync(entity, saPassword, ct);

        // Health check (timed for the ag_health_check_duration_seconds histogram).
        var healthSw = System.Diagnostics.Stopwatch.StartNew();
        var health = await agHealthChecker.CheckHealthAsync(entity, saPassword, ct);
        // The freshness of the health check, and the operations in progress, as metrics.
        metrics.AgHealthChecked(ns, name);
        metrics.SetOperationsInProgress(ns, name, OperationsInProgress(entity.Status));
        metrics.RecordAgHealthCheckDuration(ns, name, healthSw.Elapsed.TotalSeconds);

        // Failover orchestration: handle manual failover annotations and, when no primary is
        // reachable, automatically promote a secondary.
        var failover = await failoverOrchestrator.ReconcileFailoverAsync(entity, saPassword, health, ct);
        UpdateNeedsAttention(entity, failover);
        if (failover is FailoverOutcome.PlannedFailover or FailoverOutcome.ForcedFailover)
        {
            // Topology just changed; the orchestrator updated Status.PrimaryReplica/LastFailover.
            // Persist and re-observe on the next (short) cycle once the new primary settles.
            // The next pass comes within a second (see WatchedResurrection).
            return await UpdatePhase(entity, Phases.FailingOver, "FailoverExecuted",
                $"Failover to {entity.Status?.PrimaryReplica} executed; awaiting confirmation", ct, DetectedPrimaryCount(health));
        }

        if (failover is FailoverOutcome.FencingInProgress)
        {
            // The fencing state is persisted; the next pass advances it.
            return await UpdatePhase(entity, Phases.FailingOver, "FencingInProgress",
                $"Fencing the previous primary {entity.Status!.Fencing?.Pod} before promoting {entity.Status.Fencing?.Target}", ct,
                DetectedPrimaryCount(health));
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
            var primaryServesReads = await ReconcileReadOnlyFallbackAsync(entity, health, isStandard, ct);
            foreach (var replica in health.ReplicaStates)
            {
                // A replica whose role the report cannot determine must not be relabelled from a guess.
                if (replica.Role.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isPrimary = replica.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
                var (readable, readIntentReady, listenerReady) = ComputeReadRouting(replica, isPrimary);
                await agPodManager.SetReplicaLabelsAsync(replica.Name, ns, name, isPrimary ? "primary" : "secondary",
                    replica.SynchronizationHealth, replica.IsFailoverReady, readable, readIntentReady, listenerReady, ct,
                    readOnlyTarget: isPrimary ? primaryServesReads : readIntentReady);
            }
        }
        else
        {
            logger.LogWarning("AG {Name}: no primary in this health pass — keeping the current pod labels " +
                              "(the failover orchestrator drains the writer path once the unreachable threshold is reached)", name);
        }

        // Update status replicas. Convergence state only — replication lag and
        // freshness are live telemetry, served by agreplicastates (view API) and metrics.
        //
        // status.primaryReplica is the LAST KNOWN primary — the failover orchestrator
        // demotes or fences exactly that replica before promoting. A pass that reached no primary
        // used to clear it: by the time the unreachable threshold was met, the orchestrator no
        // longer knew who the primary was and promoted the target without fencing anyone, while the
        // isolated primary kept the role (two primaries; a partition that clients can still cross
        // makes them both take writes). Only a pass that finds a primary changes it.
        if (health.PrimaryReplica is { } observedPrimary)
            entity.Status.PrimaryReplica = observedPrimary;
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
        metrics.SetAgReplicaGauges(ns, name,
            total: health.ReplicaStates.Count,
            synchronized: health.ReplicaStates.Count(r => r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)),
            disconnected: health.ReplicaStates.Count(r => r.ConnectionState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase)));
        foreach (var (replica, lag) in lagByReplica)
            metrics.SetReplicaLag(ns, name, replica, lag.LogSend * 1024, lag.Redo * 1024);

        // While we hold a primary connection, reconcile read-only routing against the live topology
        // and keep REQUIRED_SYNC safe under degradation.
        if (health.PrimaryReplica != null)
        {
            var primaryFqdn = $"{health.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";
            try
            {
                await using var primaryConn = await connectionFactory.CreateProbeConnectionAsync(primaryFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                if (!isStandard && entity.Spec.ReadOnlyRouting?.Enabled == true)
                    await ReconcileReadOnlyRoutingAsync(entity, primaryConn, health, headlessService, ns, ct);
                await AdjustRequiredSyncAsync(entity, primaryConn, health, ct);
                // The backup preference used to be applied once, at initialization.
                if (!isStandard && entity.Spec.BackupPreference is { } preference)
                    await ReconcileBackupPreferenceAsync(entity, primaryConn, preference, ct);
                await ReconcileDeadReplicasAsync(entity, primaryConn, saPassword, health, headlessService, ns, isStandard, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not reconcile routing/required-sync for AG {Name}", name);
            }

            // Converge replicas that lost AG membership (e.g. recreated on a blank PV) by
            // re-joining them where their crypto survived.
            if (health.ReplicasMissingMembership.Count > 0)
                await ConvergeMissingReplicasAsync(entity, saPassword, health, headlessService, ns, ct);
        }

        // Data and log volumes grow before they fill; one that cannot is a human's call.
        var volumeFull = await volumeAutoGrower.ReconcileAsync(entity, health.ReachableReplicas, saPassword, ct);
        if (volumeFull is not null)
        {
            await eventPublisher.PublishAsync(entity, VolumeFullCondition, volumeFull, "Warning", ct);
            ConditionOps.Set(entity.Status.Conditions ??= [], VolumeFullCondition, "True", "AutoGrowBlocked", volumeFull);
        }
        else if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == VolumeFullCondition) is { Status: "True" })
            ConditionOps.Set(entity.Status.Conditions, VolumeFullCondition, "False", "Room",
                "The data and log volumes have room, or can still grow.");
        // An endpoint key that stays unusable is a human's call, raised by NeedsAttention below.
        if (slowPass)
        {
            await RecoverEndpointKeysAsync(entity, saPassword, health.ReachableReplicas, headlessService, ns, ct);
            // A key that changed since the last backup gets a new version.
            await BackupSmkForAgAsync(entity, saPassword, ns, health.ReachableReplicas, headlessService, ct);
        }
        UpdateNeedsAttention(entity, failover);

        // The database roll-up and the memory alignment move with the infrastructure cadence.
        if (slowPass)
        {
            entity.Status.DatabaseSummary = await BuildDatabaseSummaryAsync(name, ns, ct);
            await PruneSettledDivergencesAsync(entity, ns, ct);
            await TrackDurabilityAsync(entity, ct);
            await ReportColocationAsync(entity, ct);

            // Memory alignment only on replicas the health pass just reached.
            var tmplRunning = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
            if (tmplRunning.Memory != null)
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

        // Determine phase. An infrastructure failure is reported first: it is the actionable cause.
        var healthy = health.IsHealthy && infrastructureError is null;
        var newPhase = healthy ? "Running" : "Degraded";
        var reason = infrastructureError?.Reason ?? (health.IsHealthy ? "AgHealthy" : "AgDegraded");
        var message = healthy
            ? $"AG running with {health.ReplicaStates.Count} replicas"
            : infrastructureError is { } infra
                ? $"{infra.Message}{(health.IsHealthy ? "" : $"; AG degraded: {string.Join("; ", health.Warnings.Take(3))}")}"
                : $"AG degraded: {string.Join("; ", health.Warnings.Take(3))}";

        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Edition = entity.Spec.Edition;

        return await UpdatePhase(entity, newPhase, reason, message, ct, DetectedPrimaryCount(health));
    }

    /// <summary>The durability profile last fixed for each AG, read by the floors.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Durabilities = new();

    private static string DurabilityOf(SqlAvailabilityGroup ag)
        => ag.Spec.Replicas.Durability
           ?? Durabilities.GetValueOrDefault(ag.Metadata.Uid ?? ag.Metadata.Name)
           ?? Services.Control.DurabilityProfile.Effective(ag, null);

    /// <summary>
    /// The profile is fixed in the control record (at level 1, Shadow or Enforce), and a
    /// Legacy profile is said: DurabilityAtRisk.
    /// </summary>
    private async Task TrackDurabilityAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        SqlControlRecord? record = null;
        if (controlRegistry is not null && Core.Safety.SafetyLevel.Mode(1,
                Core.Safety.SafetyLevel.Effective(entity.Metadata.Annotations?.TryGetValue(Core.Safety.SafetyLevel.Annotation, out var l) == true ? l : null,
                    options.SafetyLevelMax), options.SafetyLevelMax) is not null)
        {
            try
            {
                record = await controlRegistry.EnsureAsync(entity, SqlControlRecord.ForAg(entity.Metadata.Name), null, ct);
                var profile = Services.Control.DurabilityProfile.Effective(entity, record);
                if (record.Spec.Durability?.Profile != profile)
                    await controlRegistry.UpdateAsync(record, spec => spec.Durability = new ControlDurability
                    {
                        Profile = profile,
                        RequiredSyncFloor = Services.Control.DurabilityProfile.Floor(entity, profile),
                        FixedAt = spec.Durability?.FixedAt ?? timeProvider.GetUtcNow().UtcDateTime,
                    }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG {Ag}: could not fix the durability profile in the control record", entity.Metadata.Name);
            }
        }

        var effective = Services.Control.DurabilityProfile.Effective(entity, record);
        Durabilities[entity.Metadata.Uid ?? entity.Metadata.Name] = effective;
        var conditions = entity.Status!.Conditions ??= [];
        if (effective == Services.Control.DurabilityProfile.Legacy)
            ConditionOps.Set(conditions, Services.Control.DurabilityProfile.AtRiskCondition, "True", "LegacyDurability",
                "Durability Legacy: REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT may go down to 0, and an unplanned failover can lose up to " +
                "SESSION_TIMEOUT + T_step + J of acknowledged writes. Set spec.replicas.durability: Quorum (at least 2 synchronous secondaries).");
        else if (conditions.FirstOrDefault(c => c.Type == Services.Control.DurabilityProfile.AtRiskCondition) is { Status: "True" })
            ConditionOps.Set(conditions, Services.Control.DurabilityProfile.AtRiskCondition, "False", "QuorumDurability",
                "Durability Quorum: no acknowledged commit is lost by an automatic failover.");
    }

    /// <summary>The operations in progress on the AG, with when they started.</summary>
    internal static Dictionary<string, DateTimeOffset> OperationsInProgress(SqlAvailabilityGroupStatus? status)
    {
        var operations = new Dictionary<string, DateTimeOffset>();
        if (status?.FailoverIntent?.Timestamp is { } failover)
            operations["failover"] = DateTime.SpecifyKind(failover, DateTimeKind.Utc);
        if (status?.Fencing is { } fencing)
            operations["fencing"] = DateTime.SpecifyKind(fencing.StartedAt ?? DateTime.UtcNow, DateTimeKind.Utc);
        if (status?.RollingUpdate is { Phase: not (RollingUpdatePhases.Idle or RollingUpdatePhases.Failed) } rollout)
            operations["rollingUpdate"] = DateTime.SpecifyKind(rollout.StartedAt ?? DateTime.UtcNow, DateTimeKind.Utc);
        return operations;
    }

    /// <summary>Replicas answering as PRIMARY in this health pass.</summary>
    private static int DetectedPrimaryCount(Services.Health.AgHealthReport health)
        => health.DetectedPrimaries?.Count ?? (health.PrimaryReplica is null ? 0 : 1);

    /// <summary>
    /// Converges the owned infrastructure when the spec changed or the interval elapsed,
    /// best-effort. Returns whether this was a slow pass, and a stable description of the
    /// failure, if any. A child owned by another resource still stops the whole reconcile: its pods
    /// share this AG's names, and labelling or fencing them would hit someone else's instance.
    /// </summary>
    private async Task<(bool SlowPass, (string Reason, string Message)? Error)> ConvergeInfrastructureAsync(
        SqlAvailabilityGroup entity, bool isStandard, CancellationToken ct)
    {
        var key = entity.Metadata.Uid ?? $"{entity.Metadata.NamespaceProperty}/{entity.Metadata.Name}";
        var generation = entity.Metadata.Generation ?? 0;
        var now = timeProvider.GetUtcNow();
        if (InfrastructureConverged.TryGetValue(key, out var last) && last.Generation == generation && now - last.At < InfrastructureInterval)
            return (false, null);

        var conditions = entity.Status!.Conditions ??= [];
        try
        {
            await ApplyAgInfrastructureAsync(entity, isStandard, ct);
            InfrastructureConverged[key] = (generation, now);
            ConditionOps.Set(conditions, "InfrastructureReady", "True", "Applied",
                "ConfigMap, StatefulSet, Services, PodDisruptionBudget and network policy are converged");
            return (true, null);
        }
        catch (ForeignChildException)
        {
            throw;
        }
        catch (MachineKeySecretMissingException ex)
        {
            // A condition, not a "Failed" phase; the AG self-heals when the secret returns.
            await eventPublisher.PublishAsync(entity, "MachineKeySecretMissing", ex.Message, "Warning", ct);
            ConditionOps.Set(conditions, "InfrastructureReady", "False", "MachineKeySecretMissing", ex.Message);
            return (true, ("MachineKeySecretMissing", ex.Message));
        }
        catch (StatefulSetNameNotReleasedException ex)
        {
            // The orphan-deleted StatefulSet is still terminating; the next pass re-applies it.
            ConditionOps.Set(conditions, "InfrastructureReady", "False", "StatefulSetRecreating", ex.Message);
            return (true, ("StatefulSetRecreating", ex.Message));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AG {Name}: owned infrastructure could not be converged; health checks and failover continue", entity.Metadata.Name);
            await eventPublisher.PublishAsync(entity, "InfrastructureApplyFailed",
                $"Owned resources could not be converged: {ex.Message}", "Warning", ct);
            const string message = "The owned resources could not be converged (see the InfrastructureApplyFailed event); health checks and failover continue";
            ConditionOps.Set(conditions, "InfrastructureReady", "False", "ApplyFailed", message);
            return (true, ("InfrastructureNotConverged", message));
        }
    }

    /// <summary>Applies the spec's backup preference when SQL Server reports another one.</summary>
    private async Task ReconcileBackupPreferenceAsync(SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        string preference, CancellationToken ct)
    {
        var desired = SqlAgManager.ToSqlBackupPreference(preference);
        var current = await agManager.GetBackupPreferenceAsync(primaryConn, entity.Metadata.Name, ct);
        if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            return;
        await agManager.SetBackupPreferenceAsync(primaryConn, entity.Metadata.Name, preference, ct);
        await eventPublisher.PublishAsync(entity, "BackupPreferenceApplied",
            $"AUTOMATED_BACKUP_PREFERENCE set to {desired} (was {current ?? "unknown"})", ct: ct);
    }

    public Task<ReconciliationResult<SqlAvailabilityGroup>> DeletedAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAvailabilityGroup {Name} deleted", entity.Metadata.Name);
        metrics.ForgetAg(entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        InfrastructureConverged.TryRemove(entity.Metadata.Uid ?? $"{entity.Metadata.NamespaceProperty}/{entity.Metadata.Name}", out _);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroup>.Success(entity));
    }

    // ── Read-routing & quorum helpers ─────────────────────

    /// <summary>
    /// Derive the read-routing labels for a replica: a secondary is <c>readable</c> when it is
    /// connected and its <c>ALLOW_CONNECTIONS</c> is not <c>NO</c>; it is <c>read-intent-ready</c>
    /// only when also synchronized. The primary is the writer; everything else is <c>none</c>.
    /// </summary>
    private const string ReadOnlyOnPrimaryCondition = "ReadOnlyOnPrimary";

    /// <summary>
    /// During a partition of the HADR traffic, every secondary was rightly taken
    /// off the read-only listener (their reads were frozen) while the primary was healthy: no read
    /// was served for as long as the partition lasted. When no secondary is read-intent-ready, the
    /// primary serves the reads (readOnlyListener.fallbackToPrimary, true by default) — never a
    /// disconnected secondary. Each switch is an event, and ReadOnlyOnPrimary says which path is on.
    /// </summary>
    private async Task<bool> ReconcileReadOnlyFallbackAsync(SqlAvailabilityGroup entity, Services.Health.AgHealthReport health,
        bool isStandard, CancellationToken ct)
    {
        var secondariesReady = health.ReplicaStates
            .Where(r => !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))
            .Count(r => ComputeReadRouting(r, isPrimary: false).ReadIntentReady);
        var onPrimary = !isStandard && entity.Spec.ReadOnlyListener is { FallbackToPrimary: true } && secondariesReady == 0;

        var conditions = entity.Status!.Conditions ??= [];
        var was = conditions.FirstOrDefault(c => c.Type == ReadOnlyOnPrimaryCondition) is { Status: "True" };
        if (onPrimary && !was)
        {
            const string message = "No secondary can serve reads (disconnected, not synchronized or not readable): the primary serves the read-only listener.";
            ConditionOps.Set(conditions, ReadOnlyOnPrimaryCondition, "True", "NoReadableSecondary", message);
            await eventPublisher.PublishAsync(entity, "ReadOnlyFallbackToPrimary", message, "Warning", ct);
        }
        else if (!onPrimary && was)
        {
            var message = secondariesReady > 0
                ? $"{secondariesReady} secondary replica(s) serve reads again: the read-only listener is back on them."
                : "The read-only listener no longer falls back to the primary.";
            ConditionOps.Set(conditions, ReadOnlyOnPrimaryCondition, "False", "SecondariesReadable", message);
            await eventPublisher.PublishAsync(entity, "ReadOnlyBackOnSecondaries", message, ct: ct);
        }
        return onPrimary;
    }

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

        // The list ends with the primary itself, which then serves read-intent connections
        // when no secondary of the list can.
        var endWithPrimary = entity.Spec.ReadOnlyListener is { FallbackToPrimary: true };
        if (readableSecondaries.Count == 0 && !endWithPrimary)
            return;

        // Only re-apply when the routing topology actually changed. The signature captures a
        // failover (primary changes), a replica add/remove or a readability flip (set changes) — the
        // exact transitions that require reconfiguration — avoiding redundant ALTERs every cycle.
        // Routing URLs address POD FQDNs, so the port is always the container port —
        // the listener Service port has no meaning there and produced dead routing URLs.
        var signature = $"{primary}|{string.Join(",", readableSecondaries)}|{SqlEndpoints.ContainerPort}{(endWithPrimary ? "|primary" : "")}";
        if (entity.Status?.ReadOnlyRoutingSignature == signature)
            return;

        await agManager.ConfigureReadOnlyRoutingAsync(primaryConn, name, primary, readableSecondaries,
            headlessService, ns, ct, endWithPrimary);
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

        // Honour the durability floor. Lowering this setting keeps writes flowing when a
        // synchronous secondary drops — at the price of durability: at 0, a commit is acknowledged by
        // the primary alone, and a failover from that point loses it. The README promised a floor to
        // opt out of that trade; it did not exist, and the lowering went all the way to 0 silently.
        // In Quorum the floor is at least 1 — writes block rather than become non-durable.
        var floor = Math.Max(0, Math.Min(Services.Control.DurabilityProfile.Floor(entity, DurabilityOf(entity)), Math.Max(configured, 1)));
        // Without a database the engine calls no replica HEALTHY, and there is no commit to keep
        // flowing: lowering the setting then only weakened the seeding of the first database.
        var desired = health.DatabaseStates.Count == 0
            ? configured
            : Math.Max(floor, Math.Min(configured, connectedSyncSecondaries));

        var current = await agManager.GetRequiredSyncSecondariesAsync(primaryConn, name, ct);
        if (current == desired)
            return;

        await agManager.SetRequiredSyncSecondariesAsync(primaryConn, name, desired, ct);
        if (desired < configured)
        {
            var floorNote = floor > 0
                ? $" (not below the configured durability floor of {floor})"
                : "";
            await eventPublisher.PublishAsync(entity, "RequiredSyncLowered",
                $"Lowered REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT from {configured} to {desired}{floorNote} " +
                "to keep writes available while a synchronous secondary is unavailable", "Warning", ct);
        }
        else
        {
            await eventPublisher.PublishAsync(entity, "RequiredSyncRestored",
                $"Restored REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT to {desired}", ct: ct);
        }

        if (floor > 0 && connectedSyncSecondaries < floor)
            await eventPublisher.PublishAsync(entity, "DurabilityFloorHoldingWrites",
                $"Only {connectedSyncSecondaries} synchronous secondary(ies) are connected but the durability floor is " +
                $"{floor}: REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT stays at {desired}, so commits will BLOCK until a " +
                "secondary resynchronizes. This is the configured trade-off (spec.replicas.requiredSyncFloor).", "Warning", ct);
    }

    // ── Helpers ───────────────────────────────────────────

    /// <summary>
    /// A replica whose pod was recreated with another machine key (every pod before the
    /// machine key was mounted where the engine reads it) cannot decrypt its Service Master Key; its
    /// HADR endpoint certificate is then unusable, every handshake fails with 15581, and it never
    /// rejoins. The master key is opened with its password and bound to the service master key again.
    /// When that is not enough, nothing is regenerated; EndpointKeyUsable=False says which
    /// replicas are stuck and why, and NeedsAttention carries it to a human.
    /// </summary>
    private async Task RecoverEndpointKeysAsync(SqlAvailabilityGroup entity, string saPassword,
        IReadOnlyCollection<string> reachableReplicas, string headlessService, string ns, CancellationToken ct)
    {
        var endpointName = (entity.Spec.Endpoint ?? new AgEndpointSpec()).Name;
        var masterKeyPassword = entity.Spec.MasterKeyPasswordSecretRef is { } mkRef
            ? await ResolveSecretAsync(mkRef, ns, ct)
            : saPassword;
        if (masterKeyPassword is null)
            return;

        var unusable = new List<(string Replica, EndpointKeyUnusableException Error)>();
        foreach (var replica in reachableReplicas)
        {
            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(
                    $"{replica}.{headlessService}.{ns}.svc.cluster.local", SqlEndpoints.ContainerPort, saPassword, "master", ct);
                if (await agManager.EnsureEndpointKeyUsableAsync(conn, endpointName, masterKeyPassword, ct) is { } done)
                    await eventPublisher.PublishAsync(entity, "EndpointKeyRecovered",
                        $"The HADR endpoint certificate of {replica} was unusable (its Service Master Key could not be decrypted " +
                        $"after the pod was recreated): {done}, and the endpoint was restarted.", "Warning", ct);
            }
            catch (EndpointKeyUnusableException ex)
            {
                unusable.Add((replica, ex));
                await eventPublisher.PublishAsync(entity, "EndpointKeyUnusable",
                    $"The HADR endpoint certificate of {replica} is unusable ({ex.Reason}): {ex.Message}", "Warning", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG {Ag}: could not make the endpoint key of {Replica} usable", entity.Metadata.Name, replica);
                await eventPublisher.PublishAsync(entity, "EndpointKeyUnusable",
                    $"The HADR endpoint certificate of {replica} could not be checked: {ex.Message}", "Warning", ct);
            }
        }

        var conditions = entity.Status!.Conditions ??= [];
        if (unusable.Count > 0)
        {
            var replicas = string.Join(", ", unusable.Select(u => $"{u.Replica} ({u.Error.Reason})"));
            ConditionOps.Set(conditions, EndpointKeyUsableCondition, "False", unusable[0].Error.Reason,
                $"The HADR endpoint certificate is unusable on {replicas}. The operator never regenerates the Service " +
                "Master Key, which would lose what it protects. Check first that the machine key Secret is the one the " +
                "data was created with (its sha256 annotation), then the database master key password " +
                "(masterKeyPasswordSecretRef).");
        }
        else if (conditions.FirstOrDefault(c => c.Type == EndpointKeyUsableCondition) is { Status: "False" })
            ConditionOps.Set(conditions, EndpointKeyUsableCondition, "True", "Usable",
                "The HADR endpoint certificate is usable on every reachable replica.");
    }

    /// <summary>The machine key mounted must be the one the data was created with. Its
    /// fingerprint is recorded on first sight; another key later sets MachineKeyChanged, which
    /// raises NeedsAttention.</summary>
    private async Task TrackMachineKeyAsync(SqlAvailabilityGroup entity, MachineKeyInfo machineKey, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupStatus();
        var (recorded, changed) = MachineKeyManager.CompareFingerprint(entity.Status.MachineKeySha256, machineKey);
        entity.Status.MachineKeySha256 = recorded;
        var conditions = entity.Status.Conditions ??= [];
        if (changed is not null)
        {
            await eventPublisher.PublishAsync(entity, MachineKeyChangedCondition, changed, "Warning", ct);
            ConditionOps.Set(conditions, MachineKeyChangedCondition, "True", MachineKeyChangedCondition, changed);
        }
        else if (conditions.FirstOrDefault(c => c.Type == MachineKeyChangedCondition) is { Status: "True" })
            ConditionOps.Set(conditions, MachineKeyChangedCondition, "False", "Original", "The machine key is the one the data was created with.");
    }

    private const string MachineKeyChangedCondition = "MachineKeyChanged";

    private const string EndpointKeyUsableCondition = "EndpointKeyUsable";

    private const string ReplicasColocatedCondition = "ReplicasColocated";

    /// <summary>
    /// The three replicas of the test AG ran on the one node of their architecture,
    /// and rebooting it took the whole AG down — an AG that does not survive the loss of a node,
    /// with nothing to say so. The default spread (topologySpreadConstraints, ScheduleAnyway) is
    /// only a preference: when replicas end up sharing a node, ReplicasColocated says which. A
    /// signal, not a decision to take: NeedsAttention is left alone.
    /// </summary>
    private async Task ReportColocationAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        List<V1Pod> pods;
        try
        {
            pods = await agPodManager.GetAgPodsAsync(name, entity.Metadata.NamespaceProperty, name, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "AG {Ag}: could not list the replica pods", name);
            return;
        }

        var shared = pods
            .Where(p => p.Spec?.NodeName is { Length: > 0 })
            .GroupBy(p => p.Spec.NodeName)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{string.Join(", ", g.Select(p => p.Metadata.Name).Order(StringComparer.Ordinal))} on {g.Key}")
            .ToList();
        var conditions = entity.Status!.Conditions ??= [];
        var was = conditions.FirstOrDefault(c => c.Type == ReplicasColocatedCondition) is { Status: "True" };
        if (shared.Count > 0)
        {
            var message = $"Replicas share a node ({string.Join("; ", shared)}): losing that node takes them down together. " +
                          "Add nodes, or require the spread (antiAffinity.requiredSelfAntiAffinity).";
            if (!was)
                await eventPublisher.PublishAsync(entity, ReplicasColocatedCondition, message, "Warning", ct);
            ConditionOps.Set(conditions, ReplicasColocatedCondition, "True", "SharedNode", message);
        }
        else if (was)
            ConditionOps.Set(conditions, ReplicasColocatedCondition, "False", "Spread", "Every replica runs on a node of its own.");
    }

    /// <summary>Log usage from which a database held back by a long-disconnected replica gets that
    /// replica taken out of the AG.</summary>
    internal const int HeldLogRemovalPercent = 85;

    /// <summary>
    /// A secondary on blank volumes held the whole log of the primary back
    /// (log_reuse_wait AVAILABILITY_REPLICA). No backup can truncate such a log: it grows to the end
    /// of its volume, then every write fails with 9002 — for as long as the replica stays away, which
    /// may be forever (a lost node). A replica the primary has seen DISCONNECTED for
    /// failover.removeDisconnectedReplicaAfterMinutes, while it holds back the log of an AG database
    /// at least 85% full, is taken out of the AG, which releases the log. It is added back, and
    /// re-seeded, as soon as it answers again — and so is any replica of the spec the primary does
    /// not list. Its old copy holds nothing the primary lacks: it was disconnected all along.
    /// </summary>
    private async Task ReconcileDeadReplicasAsync(SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        string saPassword, Services.Health.AgHealthReport health, string headlessService, string ns, bool isStandard, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var status = entity.Status!;
        var primary = health.PrimaryReplica!;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // How long each replica has been disconnected, from the primary's view.
        var disconnected = health.ReplicaStates
            .Where(r => !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase)
                        && r.ConnectionState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var since = status.ReplicaDisconnectedSince ?? [];
        foreach (var back in since.Keys.Where(r => !disconnected.Contains(r)).ToList())
            since.Remove(back);
        foreach (var replica in disconnected)
            since.TryAdd(replica, now);
        status.ReplicaDisconnectedSince = since.Count > 0 ? since : null;

        // The replicas of the spec the primary does not list come back as soon as they answer. The
        // primary's own list is read again: a health report is not proof enough to touch the AG.
        // Only a replica the operator took out itself, or one that holds no copy of the AG's
        // databases (a new or blank replica), is added back. Any other may hold data of its own
        // (taken out by hand, restored WITH RECOVERY): it is reported, and left alone.
        var listed = (await agManager.GetReplicaStatesAsync(primaryConn, name, ct))
            .Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listed.Contains(primary))
        {
            var topology = BuildTopology(entity, headlessService, ns, (entity.Spec.Endpoint ?? new AgEndpointSpec()).Port, isStandard);
            var drift = new List<string>();
            foreach (var replica in topology.Replicas.Where(r => !listed.Contains(r.Name) && health.ReachableReplicas.Contains(r.Name)))
            {
                var fqdn = $"{replica.Name}.{headlessService}.{ns}.svc.cluster.local";
                var removedByOperator = status.RemovedReplicas?.ContainsKey(replica.Name) == true;
                List<string>? copies = removedByOperator ? [] : await LocalCopiesAsync(entity, primaryConn, saPassword, fqdn, ct);
                if (copies is null)
                    continue;
                if (copies.Count > 0)
                    drift.Add($"{replica.Name} (holds {string.Join(", ", copies)})");
                else if (await ReaddReplicaAsync(entity, primaryConn, saPassword, replica, fqdn, ct) is { } refused)
                    drift.Add($"{replica.Name} ({refused})");
            }
            await ReportMembershipDriftAsync(entity, drift, ct);
        }

        var after = entity.Spec.Failover?.RemoveDisconnectedReplicaAfterMinutes ?? 30;
        var seeding = entity.Spec.ReplicaDefaults?.SeedingMode ?? "Automatic";
        if (after <= 0 || !seeding.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
            || status.RollingUpdate?.Phase is not (null or RollingUpdatePhases.Idle or RollingUpdatePhases.Failed))
            return;
        var dead = since.Where(kv => now - kv.Value >= TimeSpan.FromMinutes(after)).OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (dead is null)
            return;
        var held = (await agManager.GetLogsHeldByReplicasAsync(primaryConn, ct)).Where(h => h.PercentUsed >= HeldLogRemovalPercent).ToList();
        if (held.Count == 0)
            return;

        await Gateway.RemoveReplicaAsync(entity, agManager, primaryConn, name, dead, ct);
        var downSince = since[dead];
        since.Remove(dead);
        status.ReplicaDisconnectedSince = since.Count > 0 ? since : null;
        (status.RemovedReplicas ??= [])[dead] = now;
        var logs = string.Join(", ", held.Select(h => $"{h.Database} ({h.PercentUsed}% full)"));
        await eventPublisher.PublishAsync(entity, "ReplicaRemoved",
            $"Replica {dead} has been disconnected since {downSince:O} and held back the log of {logs}: it was taken out of the AG " +
            "so that the log can be truncated. It is added back and re-seeded as soon as it answers again.", "Warning", ct);
        logger.LogWarning("AG {Ag}: replica {Replica}, disconnected since {Since}, removed: it held back {Logs}", name, dead, downSince, logs);
    }

    /// <summary>The AG's databases this replica holds a copy of; null when it cannot be told this pass.</summary>
    private async Task<List<string>?> LocalCopiesAsync(SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        string saPassword, string fqdn, CancellationToken ct)
    {
        try
        {
            var agDatabases = await agManager.GetAgDatabaseNamesAsync(primaryConn, entity.Metadata.Name, ct);
            await using var conn = await connectionFactory.CreateConnectionAsync(fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
            var local = await agManager.GetUserDatabaseNamesAsync(conn, ct);
            return agDatabases.Where(d => local.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AG {Ag}: could not list the databases of {Replica}; checked again on the next pass", entity.Metadata.Name, fqdn);
            return null;
        }
    }

    /// <summary>Replicas of the spec the primary does not list, and that are not added back.</summary>
    private async Task ReportMembershipDriftAsync(SqlAvailabilityGroup entity, List<string> drift, CancellationToken ct)
    {
        var conditions = entity.Status!.Conditions ??= [];
        if (drift.Count > 0)
        {
            var message = $"Replica(s) {string.Join("; ", drift)} of the spec are not in the AG and were not added back: they may " +
                          "hold data the primary does not have. Back up or drop their copies, then the operator adds them back.";
            await eventPublisher.PublishAsync(entity, "ReplicaNotInAg", message, "Warning", ct);
            ConditionOps.Set(conditions, ReplicaMembershipDriftCondition, "True", "ReplicaNotInAg", message);
        }
        else if (conditions.FirstOrDefault(c => c.Type == ReplicaMembershipDriftCondition) is { Status: "True" })
            ConditionOps.Set(conditions, ReplicaMembershipDriftCondition, "False", "InAg",
                "Every replica of the spec is in the AG, or is being added back.");
    }

    private const string ReplicaMembershipDriftCondition = "ReplicaMembershipDrift";

    /// <summary>Adds a replica back; returns why it was refused when one of its copies could not be
    /// dropped, null otherwise — a transient failure is retried on the next pass.</summary>
    private async Task<string?> ReaddReplicaAsync(SqlAvailabilityGroup entity, Microsoft.Data.SqlClient.SqlConnection primaryConn, string saPassword,
        AgReplicaConfig replica, string fqdn, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
            // Its AG metadata and database copies predate its removal: the primary no longer holds the
            // log they would need, so they are dropped and the databases are seeded again.
            await Gateway.DropLocalAvailabilityGroupAsync(entity, agManager, conn, name, replica.Name, ct);
            var databases = await agManager.GetAgDatabaseNamesAsync(primaryConn, name, ct);
            foreach (var database in databases)
                await Gateway.DropSecondaryCopyAsync(entity, agManager, conn, database, replica.Name, ct);
            await agManager.AddReplicaAsync(primaryConn, name, replica, ct);
            // A replica without its endpoint (blank volumes) is re-provisioned by the membership convergence.
            if (await agManager.EndpointExistsAsync(conn, (entity.Spec.Endpoint ?? new AgEndpointSpec()).Name, ct))
            {
                await agManager.JoinAvailabilityGroupAsync(conn, name, ct);
                await agManager.GrantCreateAnyDatabaseAsync(conn, name, ct);
            }
            if (entity.Status!.RemovedReplicas is { } removed && removed.Remove(replica.Name) && removed.Count == 0)
                entity.Status.RemovedReplicas = null;
            await eventPublisher.PublishAsync(entity, "ReplicaReadded",
                $"Replica {replica.Name} answers again and was added back to the AG; its databases ({string.Join(", ", databases)}) are seeded from the primary.",
                "Warning", ct);
            logger.LogWarning("AG {Ag}: replica {Replica} added back", name, replica.Name);
            return null;
        }
        catch (SecondaryCopyNotDroppedException ex)
        {
            await eventPublisher.PublishAsync(entity, "SecondaryCopyNotDropped", $"Replica {replica.Name}: {ex.Message}", "Warning", ct);
            return $"its copy of {ex.Database} is online and writable";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AG {Ag}: could not add replica {Replica} back", name, replica.Name);
            await eventPublisher.PublishAsync(entity, "ReplicaReaddFailed",
                $"Replica {replica.Name} answers but could not be added back to the AG: {ex.Message}. Retried on the next pass.", "Warning", ct);
            return null;
        }
    }

    /// <summary>A replica's data or log volume is filling up and cannot grow.</summary>
    private const string VolumeFullCondition = "VolumeFull";

    /// <summary>How long after a failover the previous primary is watched for a comeback.</summary>
    private static readonly TimeSpan ResurrectionWatchWindow = TimeSpan.FromSeconds(90);

    /// <summary>Pass interval while the previous primary is watched.</summary>
    private static readonly TimeSpan ResurrectionWatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// An instance younger than this may still bring the AG online as PRIMARY: no role, or RESOLVING,
    /// is not its answer yet (AG online 1 s after "ready for client connections").
    /// </summary>
    private static readonly TimeSpan ResurrectionSettleTime = TimeSpan.FromSeconds(30);

    /// <summary>Per AG, the failover whose previous primary needs no more watching: contained, or
    /// settled in another role. Later passes do not poll it again.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ResurrectionSettled = new();

    /// <summary>The failover whose previous primary is still watched, if any.</summary>
    private (string From, string To, DateTime At, string Key)? WatchedResurrection(SqlAvailabilityGroup entity)
    {
        if (entity.Status?.LastFailover is not { From: { } from, To: { } to, Timestamp: { } at }
            || from.Equals(to, StringComparison.OrdinalIgnoreCase)
            || timeProvider.GetUtcNow().UtcDateTime - at > ResurrectionWatchWindow)
            return null;
        var key = entity.Metadata.Uid ?? $"{entity.Metadata.NamespaceProperty}/{entity.Metadata.Name}";
        return ResurrectionSettled.TryGetValue(key, out var settled) && settled == at ? null : (from, to, at, key);
    }

    /// <summary>
    /// The pod of a fenced primary is recreated and its SQL Server
    /// starts the AG as PRIMARY from its own metadata. The health pass only noticed it 10 to 25 s
    /// later; meanwhile a secondary took it for its primary — harmless after a lossless failover, it
    /// copied divergent log after a data-loss one. For a while after a failover, the AG is passed
    /// every second, and each pass first probes the previous primary (by pod IP: the DNS may still
    /// name the old pod) and takes it OFFLINE the moment it answers as PRIMARY; the secondary
    /// role follows on later passes. Waiting for it inside one pass held the pass that
    /// restores writes on the new primary (20 s of "lacks a quorum"). One answering SECONDARY
    /// ends the watch, and so does RESOLVING from an instance that has been up long enough to have
    /// settled its role (demoted in place); a freshly started one is watched until its AG comes online.
    /// </summary>
    private async Task ContainResurrectedPrimaryAsync(SqlAvailabilityGroup entity, string saPassword, CancellationToken ct)
    {
        if (WatchedResurrection(entity) is not { } watched)
            return;
        var (from, to, at, key) = watched;

        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;
        var pod = await kubernetesClient.GetAsync<V1Pod>(from, ns, ct);
        if (pod is not { Metadata.DeletionTimestamp: null, Status.PodIP: { Length: > 0 } podIp })
            return;
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(podIp, SqlEndpoints.ContainerPort,
                saPassword, "master", ct, $"{from}.{name}-headless.{ns}.svc.cluster.local");
            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                await agManager.DemoteToSecondaryAsync(conn, name, ct);
                await agPodManager.SetPodRoleLabelAsync(from, ns, name, "resolving", ct);
                await agPodManager.SetReadabilityLabelsAsync(from, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);
                await eventPublisher.PublishAsync(entity, "ResurrectedPrimaryContained",
                    $"The previous primary {from} came back as PRIMARY after the failover to {to} and was taken OFFLINE " +
                    "(RESOLVING: it accepts no write and no secondary follows it); it takes the secondary role on a later pass.",
                    "Warning", ct);
                logger.LogWarning("AG {Ag}: resurrected previous primary {From} taken OFFLINE on its first answer", name, from);
                ResurrectionSettled[key] = at;
            }
            else if (string.Equals(role, "SECONDARY", StringComparison.OrdinalIgnoreCase)
                     || await agManager.GetInstanceUptimeAsync(conn, ct) >= ResurrectionSettleTime)
                ResurrectionSettled[key] = at;
            // Otherwise it has just started and its AG is not online yet: it may still come up as PRIMARY.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "AG {Ag}: previous primary {From} not answering yet", name, from);
        }
    }

    /// <summary>
    /// NeedsAttention is set by the failover orchestrator when the AG has no writer and no
    /// automatic way out; a rollout stopped in Failed waits for a human too. Any other outcome means
    /// the operator is back in charge.
    /// </summary>
    private void UpdateNeedsAttention(SqlAvailabilityGroup entity, FailoverOutcome failover)
    {
        var conditions = entity.Status!.Conditions ??= [];
        if (failover != FailoverOutcome.AwaitingManualIntervention)
        {
            if (entity.Status.RollingUpdate is { Phase: RollingUpdatePhases.Failed })
                NeedsAttention.Require(conditions, "RollingUpdateFailed",
                    "The rolling update stopped on a failure (see its events): fix the cause, then annotate " +
                    $"{AgRollingUpdateOrchestrator.RetryAnnotation}=true to resume it.");
            else if (conditions.FirstOrDefault(c => c.Type == VolumeFullCondition) is { Status: "True" } full)
                NeedsAttention.Require(conditions, VolumeFullCondition, full.Message ?? "A data or log volume is full and cannot grow.");
            else if (conditions.FirstOrDefault(c => c.Type == MachineKeyChangedCondition) is { Status: "True" } machineKey)
                NeedsAttention.Require(conditions, MachineKeyChangedCondition, machineKey.Message ?? "The machine key changed.");
            else if (conditions.FirstOrDefault(c => c.Type == EndpointKeyUsableCondition) is { Status: "False" } key)
                NeedsAttention.Require(conditions, "EndpointKeyUnusable", key.Message ?? "The HADR endpoint certificate is unusable.");
            else if (conditions.FirstOrDefault(c => c.Type == ReplicaMembershipDriftCondition) is { Status: "True" } drift)
                NeedsAttention.Require(conditions, ReplicaMembershipDriftCondition, drift.Message ?? "A replica of the spec is not in the AG.");
            else
                NeedsAttention.Clear(conditions);
        }
        metrics.SetNeedsAttention("SqlAvailabilityGroup", entity.Metadata.NamespaceProperty, entity.Metadata.Name,
            NeedsAttention.IsRequired(conditions));
    }

    /// <summary>
    /// The replica came back blank — no master key, no
    /// certificate, no endpoint, no AG — and the operator only said so ("requires recreating the AG
    /// crypto"): the AG stayed one replica short for good. In the PerReplica model the private key
    /// never left the replica, so it is gone; the replica gets a new certificate, every other replica
    /// trusts it in place of the lost one, it trusts them, and it joins — automatic seeding does the
    /// rest. A peer that cannot be reached keeps the old trust: the next pass completes it.
    /// </summary>
    private async Task ReprovisionBlankReplicaAsync(SqlAvailabilityGroup entity, string saPassword, string replica,
        Services.Health.AgHealthReport health, string headlessService, string ns, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var endpointSpec = entity.Spec.Endpoint ?? new AgEndpointSpec();
        var masterKeyPassword = entity.Spec.MasterKeyPasswordSecretRef is { } mkRef
            ? await ResolveSecretAsync(mkRef, ns, ct)
            : saPassword;
        if (masterKeyPassword is null)
            return;

        string Fqdn(string pod) => $"{pod}.{headlessService}.{ns}.svc.cluster.local";
        var certName = $"{CertNamePrefix}{replica}";
        var loginName = $"{LoginPrefix}{replica}_login";

        await using var blank = await connectionFactory.CreateConnectionAsync(Fqdn(replica), SqlEndpoints.ContainerPort, saPassword, "master", ct);
        if (!await agManager.MasterKeyExistsAsync(blank, ct))
            await agManager.CreateMasterKeyAsync(blank, masterKeyPassword, ct);
        if (!await agManager.CertificateExistsAsync(blank, certName, ct))
        {
            var hadrSpec = entity.Spec.HadrCertificate;
            await agManager.CreateHadrCertificateAsync(blank, certName, hadrSpec?.Generate?.Subject ?? $"HADR Certificate for {replica}",
                hadrSpec?.Generate?.ExpiryDate, ct);
        }
        var certificate = await agManager.ExportHadrPublicCertificateAsync(blank, certName, ct);
        if (!await agManager.EndpointExistsAsync(blank, endpointSpec.Name, ct))
            await agManager.CreateEndpointAsync(blank, endpointSpec.Name, endpointSpec.Port, certName, endpointSpec.EncryptionAlgorithm, ct);

        var peers = Enumerable.Range(0, entity.Spec.Replicas.Count).Select(i => $"{name}-{i}")
            .Where(p => !p.Equals(replica, StringComparison.OrdinalIgnoreCase)
                        && health.ReachableReplicas.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var peer in peers)
        {
            await using var peerConn = await connectionFactory.CreateConnectionAsync(Fqdn(peer), SqlEndpoints.ContainerPort, saPassword, "master", ct);
            if (!(await agManager.ExportHadrPublicCertificateAsync(peerConn, certName, ct)).AsSpan().SequenceEqual(certificate))
            {
                await agManager.RemoveTrustedReplicaAsync(peerConn, loginName, certName, ct);
                await agManager.ImportTrustedReplicaCertificateAsync(peerConn, certName, certificate, ct);
                await agManager.CreateEndpointLoginFromCertificateAsync(peerConn, loginName, certName, ct);
            }
            await agManager.GrantEndpointConnectAsync(peerConn, endpointSpec.Name, loginName, ct);

            var peerCertName = $"{CertNamePrefix}{peer}";
            var peerLogin = $"{LoginPrefix}{peer}_login";
            if (!await agManager.CertificateExistsAsync(blank, peerCertName, ct))
                await agManager.ImportTrustedReplicaCertificateAsync(blank, peerCertName,
                    await agManager.ExportHadrPublicCertificateAsync(peerConn, peerCertName, ct), ct);
            if (!await agManager.LoginExistsAsync(blank, peerLogin, ct))
                await agManager.CreateEndpointLoginFromCertificateAsync(blank, peerLogin, peerCertName, ct);
            await agManager.GrantEndpointConnectAsync(blank, endpointSpec.Name, peerLogin, ct);
        }

        await agManager.JoinAvailabilityGroupAsync(blank, name, ct);
        await agManager.GrantCreateAnyDatabaseAsync(blank, name, ct);
        await eventPublisher.PublishAsync(entity, "ReplicaReprovisioned",
            $"Replica {replica} came back blank (AG membership, endpoint and certificate gone — its volumes were lost): it got a new " +
            $"certificate, {string.Join(", ", peers)} now trust it, and it re-joined AG {name}; automatic seeding copies the databases.",
            "Warning", ct);
        logger.LogWarning("Re-provisioned blank replica {Replica} of AG {Ag}", replica, name);
    }

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
                    await ReprovisionBlankReplicaAsync(entity, saPassword, replica, health, headlessService, ns, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
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
        var changed = false;
        var blocked = new List<string>();

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
                    var values = new Dictionary<string, int>();
                    if (currentMax != sqlMaxMb)
                        values["max server memory (MB)"] = sqlMaxMb;
                    if (sqlMinMb > 0 && currentMin != sqlMinMb)
                        values["min server memory (MB)"] = sqlMinMb;
                    await sqlServerManager.ApplyConfigurationAsync(conn, values, ct);
                    changed = true;
                    logger.LogInformation("Applied memory alignment on {Pod}: max={MaxMb} MB, min={MinMb} MB", podName, sqlMaxMb, sqlMinMb);
                }
            }
            catch (ReconfigureBlockedException ex)
            {
                // Committing our values would commit the DBA's too.
                blocked.Add($"{podName}: {string.Join(", ", ex.Pending)}");
                allAligned = false;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to apply memory alignment on replica {Pod}", podName);
                allAligned = false;
            }
        }

        var conditions = entity.Status!.Conditions ??= [];
        if (blocked.Count > 0)
        {
            var message = $"sp_configure values armed by someone else wait for a RECONFIGURE on {string.Join("; ", blocked)}: the " +
                          "operator commits nothing there until they are committed or reverted.";
            ConditionOps.Set(conditions, ReconfigureGuard.Condition, "True", ReconfigureGuard.Condition, message);
            await eventPublisher.PublishAsync(entity, ReconfigureGuard.Condition, message, "Warning", ct);
        }
        else if (allAligned)
            ReconfigureGuard.Clear(conditions);

        // An event when something was actually changed, not on every pass.
        if (allAligned && changed)
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
            summary.Synchronized = matching.Count(s => s.Status?.Phase == Phases.Synchronized);
            summary.Synchronizing = matching.Count(s => s.Status?.Phase is Phases.Synchronizing or Phases.Seeding or Phases.Adding);
            // "NotSynchronizing" is a real phase of SqlAvailabilityGroupDatabase but was
            // in no bucket — a database that had stopped replicating simply vanished from the roll-up.
            summary.Suspended = matching.Count(s => s.Status?.Phase is Phases.Suspended or Phases.NotSynchronizing);
            summary.Failed = matching.Count(s => s.Status?.Phase == Phases.Failed);
            // Per-database entries removed: the counts above are the O(1) roll-up; per-database
            // phase is served live by the aggregated APIService and by each SqlAvailabilityGroupDatabase.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to build database summary for AG {Ag}", agName);
        }
        return summary;
    }

    /// <summary>
    /// A divergence leaves openDivergences once every database of the AG has settled it
    /// (approved, or its divergent copy re-seeded). Without any SqlAvailabilityGroupDatabase, it
    /// stays: a database declared later must still be asked.
    /// </summary>
    private async Task PruneSettledDivergencesAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct)
    {
        if (entity.Status?.OpenDivergences is not { Count: > 0 } open)
            return;
        try
        {
            var databases = (await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct))
                .Where(d => d.Spec.AvailabilityGroupRef.Name == entity.Metadata.Name)
                .ToList();
            if (databases.Count == 0)
                return;
            var settled = open.Where(d => databases.All(db => db.Status?.ResolvedDivergences?.Contains(d.Id) == true)).ToList();
            foreach (var divergence in settled)
            {
                open.Remove(divergence);
                await eventPublisher.PublishAsync(entity, "DivergenceSettled",
                    $"Divergence {divergence.Id} ({divergence.From} → {divergence.To}) is settled on every database of the AG.", ct: ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AG {Ag}: could not check whether its divergences are settled", entity.Metadata.Name);
        }
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

        return new AgTopologyConfig(name, headlessService, ns, endpointPort, entity.Spec.Edition, replicas, entity.Spec.BackupPreference);
    }

    /// <summary>Records the phase and the Ready condition. SplitBrainSafe is computed from
    /// <c>detectedPrimaries</c> — how many replicas the health pass found answering as PRIMARY — never
    /// from the text of a message; null when the pass checked no health, the condition is then left
    /// as it was.</summary>
    private async Task<SqlAvailabilityGroup> UpdatePhase(SqlAvailabilityGroup entity, string phase, string reason, string message, CancellationToken ct,
        int? detectedPrimaries = null)
    {
        entity.Status ??= new SqlAvailabilityGroupStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;

        // The build every replica is meant to run. status.primaryProductVersion says what the primary
        // actually answers with — while a rolling CU upgrade is in flight, the two differ.
        var tmpl = entity.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (SqlServerImages.TryResolve(tmpl.ImageSelection(entity.Spec.Edition), out var resolution))
        {
            entity.Status.Image = resolution.Image;
            entity.Status.CumulativeUpdate = resolution.CumulativeUpdate;
            entity.Status.BaseOs = resolution.BaseOs;
        }

        // Upsert through ConditionOps on the existing list — replacing the whole
        // array dropped conditions set elsewhere and reset their transition timestamps.
        entity.Status.Conditions ??= [];
        ConditionOps.Set(entity.Status.Conditions, "Ready", phase == "Running" ? "True" : "False", reason, message);
        // The message carries only the first warnings; a split-brain reported fourth was missed.
        if (detectedPrimaries is { } primaries)
        {
            var splitBrainSafe = primaries <= 1;
            ConditionOps.Set(entity.Status.Conditions, "SplitBrainSafe", splitBrainSafe ? "True" : "False",
                splitBrainSafe ? "NoSplitBrain" : "SplitBrainDetected",
                splitBrainSafe ? "No split-brain detected" : $"{primaries} replicas answer as PRIMARY. {message}");
        }
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not check reclaimPolicy of StorageClass {Sc}", storageClassName);
            }
        }
    }

    /// <summary>Applies the StatefulSet; false when the change is held back (engine upgrade awaiting
    /// approval keeps the running image; a rollout over the fleet budget waits). The AG
    /// StatefulSet is OnDelete, but the rolling-update orchestrator replaces its pods as soon as the
    /// template changes: a template change IS the start of a rollout.</summary>
    private async Task<bool> ApplyStatefulSetAsync(V1StatefulSet sts, SqlAvailabilityGroup owner, CancellationToken ct)
    {
        // A SqlInstance may legally carry the same name as this AG, and would then own a
        // StatefulSet of the same name. Never write into it.
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, sts.Metadata.NamespaceProperty, ct);
        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"StatefulSet {sts.Metadata.NamespaceProperty}/{sts.Metadata.Name}");

        var tmpl = owner.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (!await RolloutGate.ApplyAsync(sts, existing, tmpl.ImageSelection(owner.Spec.Edition), owner.Metadata.Annotations,
                onDelete: false, owner.Status!.Conditions ??= [], owner, rolloutBudget, eventPublisher, options, ct))
            return false;

        // Storage growth is orchestrated as for a SqlInstance — every replica's PVC patched in
        // place, then the StatefulSet recreated around its pods (volumeClaimTemplates are immutable).
        // Before, any size change failed the StatefulSet apply on every pass.
        if (existing != null && await StatefulSetRecreation.ExpandVolumeClaimsAsync(kubernetesClient, logger, existing, sts, ct))
            await StatefulSetRecreation.OrphanDeleteAsync(kubernetesClient, sts.Metadata.Name, sts.Metadata.NamespaceProperty,
                "the storage expansion", ct);
        // The selector is immutable. A StatefulSet built with the former selector (version
        // and edition included) is recreated once around its pods, which it adopts — the replicas
        // keep running, no failover.
        else if (existing != null && StatefulSetRecreation.SelectorChanged(existing, sts))
        {
            await StatefulSetRecreation.OrphanDeleteAsync(kubernetesClient, sts.Metadata.Name, sts.Metadata.NamespaceProperty,
                "the selector migration", ct);
            await eventPublisher.PublishAsync(owner, "StatefulSetRecreated",
                "The StatefulSet was recreated around its pods to carry the identity-only selector; the replicas kept running.", ct: ct);
        }

        // Server-side apply — no 422 loop on immutable volumeClaimTemplates, no
        // clobbering of fields owned by other actors (webhooks, sidecar injectors).
        await kubernetesClient.ApplyAsync(sts, ct);
        return true;
    }

    /// <summary>
    /// Defence in depth for the immutability of <c>spec.replicas.count</c>. The webhook
    /// rejects the change, and a CEL rule on the CRD rejects it even when the webhook is disabled —
    /// but the reconciler is the last line: applying a new count to the StatefulSet spawns a pod on a
    /// blank PV that never joins the AG (no ADD REPLICA / JOIN is ever issued), or silently drops one
    /// — possibly the current primary, which owns the highest ordinal after a failover.
    /// </summary>
    private async Task GuardReplicaCountAsync(SqlAvailabilityGroup entity, V1StatefulSet sts, CancellationToken ct)
    {
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, sts.Metadata.NamespaceProperty, ct);
        var provisioned = existing?.Spec?.Replicas;
        if (provisioned is null || provisioned == entity.Spec.Replicas.Count)
            return;

        var message =
            $"spec.replicas.count ({entity.Spec.Replicas.Count}) differs from the {provisioned} replicas already " +
            "provisioned for this availability group. AG scale-out/in is not orchestrated (no ADD/REMOVE REPLICA), " +
            "so the change is refused: a new pod would come up on a blank volume outside the AG, and a removed one " +
            "would stay a phantom member. Restore the original count, or recreate the AG.";
        await eventPublisher.PublishAsync(entity, "ReplicaCountChangeRefused", message, "Warning", ct);
        // An infrastructure refusal, not a foreign child — the health check and failover of the
        // replicas that exist go on.
        throw new InvalidOperationException(message);
    }

    private async Task ApplyServiceAsync(V1Service svc, CancellationToken ct)
    {
        // SSA preserves clusterIP and third-party fields automatically.
        await kubernetesClient.ApplyAsync(svc, ct);
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct) =>
        await ResolveSecretAsync(entity.Spec.SaPasswordSecretRef, ns, ct);

    // Null means "absent"; transport and auth errors propagate so the runtime backs off
    // instead of acting on a wrong value (SecretValues, like every other reader).
    private Task<string?> ResolveSecretAsync(SecretReference? secretRef, string ns, CancellationToken ct)
        => SecretValues.TryGetAsync(kubernetesClient, secretRef, ns, ct);

    /// <summary>
    /// Backs up the Service Master Key of each replica into its <c>{pod}-smk-backup</c> Secret.
    /// in the slow pass too, not only at initialization: a key that changed since (a machine key
    /// lost, a manual regeneration) gets a new version, next to the earlier ones. A failure is a
    /// condition, no longer only a log line.
    /// </summary>
    private async Task BackupSmkForAgAsync(SqlAvailabilityGroup entity, string saPassword, string ns, IEnumerable<string> replicas,
        string headlessService, CancellationToken ct)
    {
        var failed = new List<string>();
        var smkPassword = await ResolveSmkPasswordForAgAsync(entity, ns, ct);
        if (smkPassword == null) return;

        foreach (var podName in replicas)
        {
            var smkSecretName = $"{podName}-smk-backup";
            try
            {
                var podFqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";
                await using var conn = await connectionFactory.CreateConnectionAsync(podFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                var result = await SmkBackups.EnsureAsync(conn, smkSecretName, ns, entity, smkPassword, smkManager, kubernetesClient,
                    timeProvider, logger, ct);
                if (result.Replaced)
                    await eventPublisher.PublishAsync(entity, "SmkBackupStale",
                        $"The service master key of {podName} changed after its latest backup: added version {result.Key} to secret " +
                        $"{smkSecretName}, the earlier ones are kept.", "Warning", ct);
            }
            catch (ForeignChildException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to backup the SMK of {Pod} — will retry", podName);
                failed.Add($"{podName}: {ex.Message}");
            }
        }

        var conditions = (entity.Status ??= new SqlAvailabilityGroupStatus()).Conditions ??= [];
        if (failed.Count > 0)
            ConditionOps.Set(conditions, "SmkBackupSucceeded", "False", "BackupFailed",
                $"The service master key could not be backed up on {string.Join("; ", failed)}");
        else
            ConditionOps.Set(conditions, "SmkBackupSucceeded", "True", "BackedUp",
                "The service master key of each replica reached is backed up to its {pod}-smk-backup secret");
    }

    private Task<string?> ResolveSmkPasswordForAgAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct)
        => KeyMaterialSecrets.GetOrCreateSmkBackupPasswordAsync(kubernetesClient, entity, entity.Spec.SmkBackupPasswordSecretRef, ns, logger, ct);
}
