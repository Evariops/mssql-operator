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
    MsSqlOperator.Services.Metrics.OperatorMetrics metrics,
    TimeProvider? timeProvider = null) : IEntityController<SqlInstance>
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
            // The StatefulSet is still terminating after an orphan delete (storage
            // expansion, selector migration). Requeue instead of applying on top of a dying object
            // (which 422'd and re-triggered the whole patch/delete cycle under backoff).
            logger.LogInformation("SqlInstance {Name}: {Message}", entity.Metadata.Name, ex.Message);
            entity = await UpdateStatus(entity, Phases.Provisioning, "StatefulSetRecreating", ex.Message, ct);
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
            entity = await UpdateStatus(entity, Phases.Failed, "SaPasswordNotFound", "SA password secret not found", ct);
            // Failed states retry explicitly instead of waiting for the periodic resync.
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(30));
        }

        // The image must come from an allowed registry, and the security contexts stay within
        // the policy (defence in depth behind admission).
        if (ImagePolicy.Refusal(entity.Spec.Image, options.AllowedImageRegistries, "spec") is { } imageRefused)
        {
            await eventPublisher.PublishAsync(entity, "ImageRegistryNotAllowed", imageRefused, "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Failed, "ImageRegistryNotAllowed", imageRefused, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromMinutes(5));
        }
        if (SecurityContextPolicy.Check(entity.Spec.PodSecurityContext, entity.Spec.SecurityContext, "spec") is { } contextRefused)
        {
            await eventPublisher.PublishAsync(entity, "SecurityContextNotAllowed", contextRefused, "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Failed, "SecurityContextNotAllowed", contextRefused, ct);
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
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, entity.Spec.Memory, entity.Spec.Storage, entity.Spec.RuntimeClassName,
            entity.Spec.Tls);
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
            var machineKey = await machineKeyManager.EnsureMachineKeySecretAsync(
                name, ns, ownerRef,
                entity.Spec.MachineKeySecretRef?.Name, entity.Spec.MachineKeySecretRef?.Key, ct);
            machineKeySecretName = machineKey.SecretName;
            await TrackMachineKeyAsync(entity, machineKey, ct);
        }
        catch (MachineKeySecretMissingException ex)
        {
            // Explicit reference broken → visible condition + retry, zero key generated.
            await eventPublisher.PublishAsync(entity, "MachineKeySecretMissing", ex.Message, "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Failed, "MachineKeySecretMissing", ex.Message, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(60));
        }

        if (await TlsSecret.MissingAsync(kubernetesClient, entity.Spec.Tls, ns, ct) is { } tlsMissing)
        {
            await eventPublisher.PublishAsync(entity, "TlsSecretMissing", tlsMissing, "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Failed, "TlsSecretMissing", tlsMissing, ct);
            return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // Build and apply StatefulSet (uses configHash for rolling restart detection)
        var sts = statefulSetBuilder.Build(entity, configHash, machineKeySecretName);
        ChildOwnershipGuard.StampOwner(sts, entity);
        await OwnedChildren.GuardExistingDataVolumesAsync(kubernetesClient, eventPublisher, logger, entity, sts, replicas: 1, ct);
        if (await ApplyStatefulSetAsync(sts, entity, ct))
        {
            // Written first, a ConfigMap pointing masterdatafile/errorlogfile at a volume the
            // refused StatefulSet never mounted was read at the next restart (a crash, or a blank master
            // on a writable root filesystem).
            await OwnedChildren.ApplyMssqlConfAsync(kubernetesClient, entity, OperatorLabels.Instance, confContent, ct);
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
            await OwnedChildren.ApplyPodDisruptionBudgetAsync(kubernetesClient, logger, entity, labels, maxUnavailable: 0, ct);

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
            var pod = await kubernetesClient.GetAsync<V1Pod>($"{name}-0", ns, ct);
            if (SchedulingDiagnostics.UnschedulableReason(pod) is { } unschedulable)
            {
                await eventPublisher.PublishAsync(entity, "SchedulingBlocked",
                    $"Pod {name}-0 cannot be scheduled: {unschedulable}", "Warning", ct);
                entity = await UpdateStatus(entity, Phases.Provisioning, "SchedulingBlocked",
                    $"Pod {name}-0 unschedulable: {unschedulable}", ct, endpoint);
                return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(15));
            }

            // A pod that cannot come back on its own — its node is gone, or it is stuck
            // Terminating — is a human's call, not a wait.
            var node = pod?.Spec?.NodeName is { Length: > 0 } nodeName
                ? await kubernetesClient.GetAsync<V1Node>(nodeName, null, ct)
                : null;
            if (StuckPod(pod, node, Clock.GetUtcNow().UtcDateTime) is { } stuck)
            {
                entity = await UpdateStatus(entity, Phases.Provisioning, PodStuckReason, stuck, ct, endpoint);
                return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(15));
            }

            entity = await UpdateStatus(entity, Phases.Provisioning, "WaitingForSqlServer",
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
                // Server-side apply — no GET+PUT that wrote back what others had set.
                await kubernetesClient.ApplyAsync(netpol, ct);
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
        entity = await UpdateStatus(entity, Phases.Running, "InstanceRunning", "SQL Server instance is running", ct, endpoint, productVersion);

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
        if (sts?.Metadata.Annotations?.ContainsKey(LegacySaPasswordHashAnnotation) != true)
            return;
        try
        {
            await kubernetesClient.PatchAsync(sts,
                [JsonPatch.Remove("/metadata/annotations/" + JsonPatch.Escape(LegacySaPasswordHashAnnotation))], ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not remove the legacy sa password hash annotation of {Name}", name);
        }
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
        if (existing != null && await StatefulSetRecreation.ExpandVolumeClaimsAsync(kubernetesClient, logger, existing, sts, ct))
            await StatefulSetRecreation.OrphanDeleteAsync(kubernetesClient, sts.Metadata.Name, ns, "the storage expansion", ct);
        // The selector is immutable too. A StatefulSet built with the former selector
        // (version and edition included) is recreated once around its pods, which it adopts.
        else if (existing != null && StatefulSetRecreation.SelectorChanged(existing, sts))
        {
            await StatefulSetRecreation.OrphanDeleteAsync(kubernetesClient, sts.Metadata.Name, ns, "the selector migration", ct);
            await eventPublisher.PublishAsync(owner, "StatefulSetRecreated",
                "The StatefulSet was recreated around its pods to carry the identity-only selector; the pods kept running.", ct: ct);
        }

        // Server-side apply — no 422 loop on immutable fields, no clobbering of
        // fields owned by other actors (webhooks, sidecar injectors).
        await kubernetesClient.ApplyAsync(sts, ct);
        return true;
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
            return await SqlProbes.GetProductVersionAsync(conn, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "SqlInstance {Name} pod is Ready but SQL is not serving yet", name);
            return null;
        }
    }

    /// <summary>The machine key mounted must be the one the data was created with. Its
    /// fingerprint is recorded on first sight; another key later sets MachineKeyChanged.</summary>
    private async Task TrackMachineKeyAsync(SqlInstance entity, MachineKeyInfo machineKey, CancellationToken ct)
    {
        entity.Status ??= new SqlInstanceStatus();
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

    internal const string MachineKeyChangedCondition = "MachineKeyChanged";

    private const string PodStuckReason = "PodStuck";

    /// <summary>How long a pod may stay not ready on a NotReady node, or Terminating, before
    /// a human is called.</summary>
    internal static readonly TimeSpan StuckPodAfter = TimeSpan.FromMinutes(5);

    private TimeProvider Clock => timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Why the pod cannot come back on its own, or null. Kubernetes does not recreate a
    /// StatefulSet pod whose node stopped answering: it stays Terminating, or not ready on a NotReady
    /// node, until someone confirms the node is gone.
    /// </summary>
    internal static string? StuckPod(V1Pod? pod, V1Node? node, DateTime now)
    {
        if (pod is null)
            return null;
        var podName = pod.Metadata.Name;
        if (pod.Metadata.DeletionTimestamp is { } deleting && now - deleting.ToUniversalTime() > StuckPodAfter)
            return $"Pod {podName} has been Terminating since {deleting.ToUniversalTime():O}: its node " +
                   $"{pod.Spec?.NodeName} does not confirm the stop. Check the node; once it is known to be down, " +
                   "force-delete the pod so the StatefulSet recreates it.";

        var ready = pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready");
        if (ready is { Status: "True" })
            return null;
        if ((ready?.LastTransitionTime ?? pod.Metadata.CreationTimestamp) is not { } since || now - since.ToUniversalTime() <= StuckPodAfter)
            return null;
        if (node?.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready") is { Status: not "True" })
            return $"Pod {podName} has not been ready since {since.ToUniversalTime():O} and its node {node.Metadata.Name} is " +
                   "NotReady: Kubernetes will not move it. Bring the node back, or confirm it is down and force-delete the pod.";
        return null;
    }

    /// <summary>
    /// The instance had no NeedsAttention — a lost machine key, a sa login stuck on an unknown
    /// password, a pod stuck on a dead node were only seen by whoever looked at the right condition.
    /// </summary>
    private void UpdateNeedsAttention(SqlInstance entity, string reason, string message)
    {
        var conditions = entity.Status!.Conditions ??= [];
        if (reason == "MachineKeySecretMissing")
            NeedsAttention.Require(conditions, reason, message);
        else if (conditions.FirstOrDefault(c => c.Type == MachineKeyChangedCondition) is { Status: "True" } machineKey)
            NeedsAttention.Require(conditions, MachineKeyChangedCondition, machineKey.Message ?? "The machine key changed.");
        else if (conditions.FirstOrDefault(c => c.Type == "SaPasswordConverged") is { Status: "False", Reason: nameof(SaConvergence.Failed) })
            NeedsAttention.Require(conditions, "SaPasswordNotConverged",
                "SQL Server refuses both the sa password of the referenced Secret and the last one applied: set it by hand " +
                "(see the events), the operator cannot log in.");
        else if (reason == PodStuckReason)
            NeedsAttention.Require(conditions, PodStuckReason, message);
        else
            NeedsAttention.Clear(conditions);
        metrics.SetNeedsAttention("SqlInstance", entity.Metadata.NamespaceProperty, entity.Metadata.Name,
            NeedsAttention.IsRequired(conditions));
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
        UpdateNeedsAttention(entity, reason, message);
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
                var values = new Dictionary<string, int>();
                if (currentMax != sqlMaxMb)
                    values["max server memory (MB)"] = sqlMaxMb;
                if (currentMin != sqlMinMb && sqlMinMb > 0)
                    values["min server memory (MB)"] = sqlMinMb;
                await sqlServerManager.ApplyConfigurationAsync(conn, values, ct);
                logger.LogInformation("Applied max server memory = {MaxMb} MB, min = {MinMb} MB on {Name} (pod limit: {TotalMb} MB, reserve: {ReserveMb} MB)",
                    sqlMaxMb, sqlMinMb, name, totalMb, reserveMb);

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
            ReconfigureGuard.Clear(entity.Status.Conditions);
        }
        catch (ReconfigureBlockedException ex)
        {
            // Committing our values would commit the DBA's too.
            entity.Status ??= new SqlInstanceStatus();
            ReconfigureGuard.Report(entity.Status.Conditions ??= [], ex);
            ConditionOps.Set(entity.Status.Conditions, "MemoryAligned", "False", ReconfigureGuard.Condition, ex.Message);
            await eventPublisher.PublishAsync(entity, ReconfigureGuard.Condition, ex.Message, "Warning", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        var smkSecretName = $"{name}-smk-backup";
        entity.Status ??= new SqlInstanceStatus();
        try
        {
            // Resolve or generate SMK backup password
            var smkPassword = await ResolveSmkPasswordAsync(entity, ns, ct);
            if (smkPassword == null) return;

            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            // A key that changed since the latest backup gets a new version; the
            // earlier ones stay, in case the change was the accident.
            var result = await SmkBackups.EnsureAsync(conn, smkSecretName, ns, entity, smkPassword, smkManager, kubernetesClient,
                TimeProvider.System, logger, ct);
            if (result.Replaced)
                await eventPublisher.PublishAsync(entity, "SmkBackupStale",
                    $"The service master key changed after the latest backup was taken: added version {result.Key} to secret " +
                    $"{smkSecretName}, the earlier ones are kept.", "Warning", ct);
            ConditionOps.Set(entity.Status.Conditions ??= [], "SmkBackupSucceeded", "True", "BackedUp",
                $"Service master key backed up to secret {smkSecretName}" + (result.Key is { } key ? $" ({key})" : ""));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to backup SMK for {Name} — will retry on next reconciliation", name);
            ConditionOps.Set(entity.Status.Conditions ??= [], "SmkBackupSucceeded", "False",
                "BackupFailed", $"The service master key could not be backed up to secret {smkSecretName}: {ex.Message}");
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read the full-text state of {Name}", name);
        }
    }

    private Task<string?> ResolveSmkPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
        => KeyMaterialSecrets.GetOrCreateSmkBackupPasswordAsync(kubernetesClient, entity, entity.Spec.SmkBackupPasswordSecretRef, ns, logger, ct);


}
