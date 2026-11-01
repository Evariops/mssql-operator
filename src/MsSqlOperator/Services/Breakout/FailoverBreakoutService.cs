using MsSqlOperator.Services.Actuation;
using System.Diagnostics;
using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.Breakout;

public class FailoverBreakoutService(
    ILogger<FailoverBreakoutService> logger,
    IKubernetesClient kubernetesClient,
    IFencingDriver fencingDriver,
    IBlacklistManager blacklistManager,
    IEventPublisher eventPublisher,
    BreakoutMetrics metrics,
    IOptions<BreakoutOptions> options,
    TimeProvider timeProvider,
    IKubernetes? rawClient = null,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null) : BackgroundService
{
    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation("Failover Breakout Controller started (period={Period}s, threshold={Threshold}s, driver={Driver})",
            opts.ControlLoopPeriodSeconds, opts.TerminatingThresholdSeconds, opts.FencingDriver);

        // The blacklist's single-writer safety depends on leader
        // election. A configuration that disables it must fail fast, not run silently unsafe.
        if (!opts.LeaderElection)
            throw new InvalidOperationException(
                "breakout.enabled=true requires leader election. Either re-enable breakout leader election or disable the breakout component.");

        // fencingDriver=none means ZERO protection — never let it be silent.
        if (IsDryRun)
            logger.LogWarning(
                "Breakout is running with fencingDriver=none: no VolumeAttachment is ever detached and no pod is ever force-deleted. " +
                "This is observation mode only — do NOT rely on it for split-brain protection in production.");

        // Without a raw client wired (test seam) run directly. With it, only the replica
        // holding the Lease ever fences, so a scaled/rolled breakout deployment can't double-fence
        // or race on the blacklist ConfigMap.
        if (rawClient is null)
        {
            await RunControlLoopAsync(stoppingToken);
            logger.LogInformation("Failover Breakout Controller stopped");
            return;
        }

        var leaseLock = new LeaseLock(rawClient, opts.BlacklistConfigMapNamespace, opts.LeaseName,
            $"{Environment.MachineName}-{Guid.NewGuid():N}");
        var config = new LeaderElectionConfig(leaseLock)
        {
            LeaseDuration = TimeSpan.FromSeconds(opts.LeaseDurationSeconds),
            RenewDeadline = TimeSpan.FromSeconds(opts.LeaseRenewDeadlineSeconds),
            RetryPeriod = TimeSpan.FromSeconds(opts.LeaseRetryPeriodSeconds),
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            using var leaderScope = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var elector = new LeaderElector(config);

            var loop = Task.CompletedTask;
            elector.OnStartedLeading += () =>
            {
                logger.LogInformation("Breakout acquired leadership; starting control loop");
                loop = RunControlLoopAsync(leaderScope.Token);
            };
            elector.OnStoppedLeading += () =>
            {
                logger.LogWarning("Breakout lost leadership; stopping control loop");
                leaderScope.Cancel();
            };

            try
            {
                await elector.RunUntilLeadershipLostAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Breakout leader election error; retrying");
            }
            finally
            {
                leaderScope.Cancel();
                try
                {
                    // The loop must have stopped before another replica can take the Lease.
                    await loop.WaitAsync(TimeSpan.FromSeconds(opts.LostLeadershipStopSeconds), CancellationToken.None);
                }
                catch (OperationCanceledException) { /* loop cancelled */ }
                catch (TimeoutException)
                {
                    logger.LogCritical("The breakout control loop did not stop within {Seconds}s of losing the Lease; exiting hard",
                        opts.LostLeadershipStopSeconds);
                    Environment.Exit(1);
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(config.RetryPeriod, stoppingToken);
        }

        logger.LogInformation("Failover Breakout Controller stopped");
    }

    /// <summary>The pods in Terminating, from a watch; null without a raw client (tests list).</summary>
    private TerminatingPodTracker? _tracker;

    private async Task RunControlLoopAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var period = TimeSpan.FromSeconds(opts.ControlLoopPeriodSeconds);
        var watches = Task.CompletedTask;
        if (rawClient is not null)
        {
            _tracker = new TerminatingPodTracker(2);
            watches = Task.WhenAll(WatchPods(opts.PodSelector, ct), WatchPods(opts.PodSelectorAg, ct));
        }

        while (!ct.IsCancellationRequested)
        {
            // Woken by a change of the pods in Terminating; the period is a safety net, and
            // what lets relocationDelay elapse.
            try
            {
                if (_tracker is not null)
                    await _tracker.WaitAsync(period, ct);
                else
                    await Task.Delay(period, timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunPassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error in failover breakout control loop");
            }
        }
        try { await watches; }
        catch (OperationCanceledException) { /* stopped with the loop */ }
    }

    /// <summary>The watch of the SQL pods of one selector, feeding the tracker.</summary>
    private Task WatchPods(string selector, CancellationToken ct)
    {
        var raw = rawClient!;
        var kind = $"Pod ({selector})";
        var watcher = new MsSqlOperator.Operator.Runtime.ResourceWatcher(kind, logger,
            c => MsSqlOperator.Operator.Runtime.ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListPodForAllNamespacesAsync(
                labelSelector: selector, continueParameter: next, limit: MsSqlOperator.Operator.Runtime.ListPager.PageSize, cancellationToken: t)
                .ConfigureAwait(false), c),
            (rv, onAct, emit, c) => MsSqlOperator.Operator.Runtime.K8sWatchSession.RunAsync(
                raw.CoreV1.WatchListPodForAllNamespacesAsync(labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: 300,
                    resourceVersion: rv, cancellationToken: c), kind, logger, onAct, emit, c),
            items => _tracker!.Relist(selector, items),
            (type, obj) => _tracker!.Update(selector, type, obj),
            timeProvider);
        return watcher.RunAsync(ct);
    }

    private bool IsDryRun => string.Equals(options.Value.FencingDriver, "none", StringComparison.OrdinalIgnoreCase);

    private int _loopCount;

    /// <summary>
    /// One pass of the loop, bounded by <see cref="BreakoutOptions.PassDeadlineSeconds"/>. Returns
    /// false when the deadline cut it short; a fencing interrupted half-way is taken up again by the
    /// next pass (the pod is still Terminating on its NotReady node).
    /// </summary>
    internal async Task<bool> RunPassAsync(CancellationToken ct)
    {
        var deadline = TimeSpan.FromSeconds(options.Value.PassDeadlineSeconds);
        using var expiry = new CancellationTokenSource(deadline, timeProvider);
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        try
        {
            await RunLoopAsync(pass.Token);
            return true;
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogError("A breakout pass exceeded its {Deadline}s deadline and was cut short; the next pass starts over",
                deadline.TotalSeconds);
            return false;
        }
    }

    internal async Task RunLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Periodic reminder (~every 5 min at the default 5s period) that the
        // dry-run driver provides no protection.
        if (IsDryRun && _loopCount++ % 60 == 0)
            logger.LogWarning("fencingDriver=none — breakout is in observation mode, no fencing protection is active");

        await ProcessTerminatingPodsAsync(ct);
        await ProcessUnfencingAsync(ct);
        metrics.SetFencedNodes((await blacklistManager.GetBlacklistedNodesAsync(ct)).Count);

        sw.Stop();
        metrics.RecordLoopDuration(sw.Elapsed.TotalSeconds);
    }

    internal async Task ProcessTerminatingPodsAsync(CancellationToken ct)
    {
        var opts = options.Value;
        List<V1Pod> terminatingPods;
        if (_tracker is { Seeded: true } tracker)
            terminatingPods = [.. tracker.Snapshot()]; // From the watch
        else
        {
            var pods = await kubernetesClient.ListAsync<V1Pod>((string?)null, opts.PodSelector, ct) ?? [];
            var agPods = await kubernetesClient.ListAsync<V1Pod>((string?)null, opts.PodSelectorAg, ct) ?? [];
            terminatingPods = pods.Concat(agPods)
                .Where(p => p.Metadata.DeletionTimestamp.HasValue)
                .DistinctBy(p => p.Metadata.Uid)
                .ToList();
        }

        if (terminatingPods.Count == 0)
            return;

        // Enforce max concurrent fencing — but only count *recent* fencings, so nodes stuck
        // NotReady forever (dead hardware) don't permanently saturate the limit.
        var blacklisted = await blacklistManager.GetBlacklistedNodesAsync(ct);
        var windowStart = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(opts.FencingRateWindowSeconds);
        var currentFencedCount = blacklisted.Count(kv => kv.Value >= windowStart);

        foreach (var pod in terminatingPods)
        {
            if (currentFencedCount >= opts.MaxConcurrentFencing)
            {
                logger.LogWarning("Max concurrent fencing limit ({Max}) reached, skipping remaining pods", opts.MaxConcurrentFencing);
                break;
            }

            // The reconcile-paused escape hatch exists so a human can perform manual
            // surgery (split-brain recovery, SMK restore) without the operator acting underneath. The
            // breakout controller is a separate loop and ignored it entirely — it would still taint the
            // node, tear the volumes away and force-delete the pod in the middle of that surgery.
            if (await IsOwnerPausedAsync(pod, ct))
            {
                logger.LogInformation(
                    "Pod {PodName} belongs to a resource with {Annotation}=true; the breakout will not fence it",
                    pod.Metadata.Name, Operator.OperatorLabels.PausedAnnotation);
                continue;
            }

            try
            {
                var fenced = await ProcessPodAsync(pod, ct);
                if (fenced) currentFencedCount++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error processing terminating pod {PodName}", pod.Metadata.Name);
            }
        }
    }

    /// <summary>True when the SqlInstance / SqlAvailabilityGroup owning this pod is paused
    /// Also when the owner cannot be read — fencing a pod a human may be
    /// operating on by hand cannot be undone; waiting one more pass can.</summary>
    private async Task<bool> IsOwnerPausedAsync(V1Pod pod, CancellationToken ct)
    {
        var labels = pod.Metadata.Labels;
        if (labels is null) return false;

        try
        {
            if (labels.TryGetValue(Operator.OperatorLabels.Ag, out var agName))
            {
                var ag = await kubernetesClient.GetAsync<Entities.V1Alpha1.SqlAvailabilityGroup>(
                    agName, pod.Metadata.NamespaceProperty, ct);
                return IsPaused(ag?.Metadata?.Annotations);
            }

            if (labels.TryGetValue(Operator.OperatorLabels.Instance, out var instanceName))
            {
                var instance = await kubernetesClient.GetAsync<Entities.V1Alpha1.SqlInstance>(
                    instanceName, pod.Metadata.NamespaceProperty, ct);
                return IsPaused(instance?.Metadata?.Annotations);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the owner of pod {PodName} to check the pause annotation; not fencing it this pass",
                pod.Metadata.Name);
            return true;
        }

        return false;
    }

    private static bool IsPaused(IDictionary<string, string>? annotations)
        => annotations?.TryGetValue(Operator.OperatorLabels.PausedAnnotation, out var v) == true
           && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    internal async Task<bool> ProcessPodAsync(V1Pod pod, CancellationToken ct)
    {
        var opts = options.Value;
        var podName = pod.Metadata.Name;
        var nodeName = pod.Spec.NodeName;

        if (string.IsNullOrEmpty(nodeName))
        {
            logger.LogDebug("Pod {PodName} has no node assignment, skipping", podName);
            return false;
        }

        // Safety: check terminating duration.
        //
        // The threshold must clear the pod's OWN grace period. The default terminating
        // threshold is 30s while SQL Server pods get terminationGracePeriodSeconds=180 — so a pod
        // shutting down perfectly normally (a rolling update, a drain) looked "stuck" after 30s. If the
        // node's kubelet also flapped NotReady in that window, the breakout tore the volumes away from
        // an instance in the middle of its final checkpoint.
        var terminatingDuration = timeProvider.GetUtcNow() - pod.Metadata.DeletionTimestamp!.Value.ToUniversalTime();
        var gracePeriod = pod.Spec?.TerminationGracePeriodSeconds ?? 0;
        var effectiveThreshold = Math.Max(opts.TerminatingThresholdSeconds, gracePeriod + opts.TerminatingThresholdSeconds);
        if (terminatingDuration.TotalSeconds < effectiveThreshold)
        {
            logger.LogDebug("Pod {PodName} has been Terminating for {Duration}s, below the effective threshold {Threshold}s " +
                            "(grace period {Grace}s + {Configured}s)",
                podName, (int)terminatingDuration.TotalSeconds, effectiveThreshold, gracePeriod, opts.TerminatingThresholdSeconds);
            return false;
        }

        // Safety: check pod owner is StatefulSet
        if (!IsOwnedByStatefulSet(pod))
        {
            logger.LogDebug("Pod {PodName} is not owned by a StatefulSet, skipping", podName);
            return false;
        }

        // Safety: check node is NotReady
        if (!await IsNodeNotReadyAsync(nodeName, ct))
        {
            logger.LogDebug("Node {Node} for pod {PodName} is Ready, skipping", nodeName, podName);
            return false;
        }

        // Relocation, never fencing of the node — no taint. The node's Lease must have stayed
        // unchanged for relocationDelay, by the operator's clock (no comparison with the node's).
        var lease = await kubernetesClient.GetAsync<V1Lease>(nodeName, "kube-node-lease", ct);
        var renew = lease?.Spec?.RenewTime?.ToUniversalTime().ToString("O") ?? "none";
        var now = timeProvider.GetUtcNow();
        var frozenSince = _leaseFrozenSince.AddOrUpdate(nodeName, (renew, now),
            (_, previous) => previous.Renew == renew ? previous : (renew, now)).Since;
        if (now - frozenSince < TimeSpan.FromSeconds(opts.RelocationDelaySeconds))
        {
            logger.LogDebug("Node {Node} of pod {PodName}: Lease unchanged for {Seconds}s, below relocationDelay {Delay}s",
                nodeName, podName, (int)(now - frozenSince).TotalSeconds, opts.RelocationDelaySeconds);
            return false;
        }

        // Only storage whose detachment withdraws the node's access is relocated.
        var drivers = await CsiDriversOfAsync(pod, ct);
        var untrusted = drivers.Where(d => !opts.TrustedFencingCsiDrivers.Contains(d, StringComparer.Ordinal)).ToList();
        if (drivers.Count == 0 || untrusted.Count > 0)
        {
            await PublishEventAsync(pod, "RelocationRefused",
                $"Pod {podName} is stuck on NotReady node {nodeName} but its storage ({(drivers.Count == 0 ? "not CSI" : string.Join(", ", untrusted))}) " +
                "is not in breakout.trustedFencingCsiDrivers: detaching it may not withdraw the node's access. A human decides " +
                "(runbook docs/failure-modes.md#a-node-is-lost).", "Warning", ct);
            return false;
        }

        if (await blacklistManager.IsBlacklistedAsync(nodeName, ct))
        {
            logger.LogDebug("Node {Node} already counted, relocating pod {PodName}", nodeName, podName);
            await FenceAndDeletePodAsync(pod, ct);
            return false; // Node already counted
        }

        logger.LogWarning("Relocating pod {PodName} from lost node {Node} (Lease unchanged for {Seconds}s)",
            podName, nodeName, (int)(now - frozenSince).TotalSeconds);

        await blacklistManager.AddNodeAsync(nodeName, ct);
        metrics.RecordFence(nodeName);
        await FenceAndDeletePodAsync(pod, ct);

        var dryRun = string.Equals(options.Value.FencingDriver, "none", StringComparison.OrdinalIgnoreCase);
        await PublishEventAsync(pod,
            dryRun ? "PodRelocationSimulated" : "PodRelocated",
            dryRun
                ? $"[DRY-RUN] Pod {podName} would be relocated from lost node {nodeName} (fencingDriver=none)"
                : $"Pod {podName} relocated from lost node {nodeName}: its VolumeAttachments were removed, then the pod",
            "Warning", ct);

        return true;
    }

    /// <summary>Per node, the last renewTime seen on its Lease and since when (operator clock).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Renew, DateTimeOffset Since)> _leaseFrozenSince = new();

    /// <summary>The CSI driver of every volume of the pod, through its claims and their volumes.</summary>
    internal async Task<List<string>> CsiDriversOfAsync(V1Pod pod, CancellationToken ct)
    {
        var drivers = new List<string>();
        foreach (var claim in (pod.Spec?.Volumes ?? []).Select(v => v.PersistentVolumeClaim?.ClaimName).OfType<string>())
        {
            var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(claim, pod.Metadata.NamespaceProperty, ct);
            if (pvc?.Spec?.VolumeName is not { } pvName)
                return [];
            var pv = await kubernetesClient.GetAsync<V1PersistentVolume>(pvName, null, ct);
            if (pv?.Spec?.Csi?.Driver is not { } driver)
                return [];
            drivers.Add(driver);
        }
        return drivers.Distinct(StringComparer.Ordinal).ToList();
    }

    private readonly HashSet<string> _detachRequested = [];

    private static string DetachKey(V1VolumeAttachment va) => va.Metadata.Uid ?? va.Metadata.Name;

    private async Task FenceAndDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;
        var nodeName = pod.Spec.NodeName;

        // Step 1: Find and detach VolumeAttachments
        var volumeAttachments = await FindVolumeAttachmentsForPodAsync(pod, ct);

        if (volumeAttachments.Count > 0)
        {
            // Only ask for a detach we have not already asked for. A CSI driver that never
            // confirms (the normal case on genuinely dead hardware) kept this loop blocking for ~30s per
            // attachment, on every pass, delaying the fencing of every other pod behind it.
            var toDetach = volumeAttachments
                .Where(va => !_detachRequested.Contains(DetachKey(va)))
                .ToList();

            if (toDetach.Count > 0)
            {
                await Gateway.DetachVolumesAsync(pod, fencingDriver, toDetach, ct);
                foreach (var va in toDetach)
                {
                    _detachRequested.Add(DetachKey(va));
                    metrics.RecordVolumeDetach(podName);
                }

                await PublishEventAsync(pod, "VolumeDetached",
                    $"Detached {toDetach.Count} volume(s) for pod {podName} from node {nodeName}", ct: ct);
            }

            // Verify detachment (a cheap LIST; no waiting here — the next loop turn re-checks).
            var remaining = await FindVolumeAttachmentsForPodAsync(pod, ct);
            if (remaining.Count > 0)
            {
                logger.LogWarning(
                    "Post-detach verification: {Count} VolumeAttachment(s) still present for pod {PodName}; deferring the pod deletion " +
                    "(the CSI external-attacher has not run ControllerUnpublishVolume yet)",
                    remaining.Count, podName);
                return; // Do NOT delete the pod until VAs are truly gone
            }

            foreach (var va in volumeAttachments)
                _detachRequested.Remove(DetachKey(va));
        }

        // Step 2: Force-delete the pod
        await Gateway.ForceDeletePodAsync(pod, fencingDriver, ct);
        metrics.RecordForceDelete(podName);

        await PublishEventAsync(pod, "ForcedDeletion",
            $"Pod {podName} force-deleted after volume detachment", "Warning", ct);

        logger.LogWarning("Pod {PodName} force-deleted from fenced node {Node}", podName, nodeName);
    }

    internal const string FencedTaintKey = "mssql-operator.evariops.com/fenced";

    // The breakout never taints a node any more — a NoExecute taint acted on every workload of
    // the node, other teams' volumes included. The taints of earlier versions are removed by the
    // unfencing below once their node is Ready again.

    private async Task UntaintNodeAsync(V1Node node, CancellationToken ct)
    {
        var taints = node.Spec?.Taints;
        if (taints == null) return;
        if (!taints.Any(t => t.Key == FencedTaintKey)) return;
        await kubernetesClient.PatchAsync(node, [JsonPatch.Replace("/spec/taints", taints.Where(t => t.Key != FencedTaintKey).ToList())], ct);
        logger.LogInformation("Removed {Taint} taint from node {Node}", FencedTaintKey, node.Metadata.Name);
    }

    internal async Task ProcessUnfencingAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var blacklisted = await blacklistManager.GetBlacklistedNodesAsync(ct);

        foreach (var (nodeName, fencedAt) in blacklisted)
        {
            try
            {
                var node = await kubernetesClient.GetAsync<V1Node>(nodeName, null, ct);
                if (node == null)
                {
                    logger.LogInformation("Blacklisted node {Node} no longer exists, removing from blacklist", nodeName);
                    await blacklistManager.RemoveNodeAsync(nodeName, ct);
                    metrics.RecordUnfence(nodeName);
                    continue;
                }

                if (!IsNodeReady(node))
                    continue;

                // Check stability: node must be Ready for > grace period
                var readySince = GetReadyTransitionTime(node);
                if (readySince == null)
                    continue;

                var readyDuration = timeProvider.GetUtcNow() - readySince.Value;
                if (readyDuration.TotalSeconds < opts.NodeReadyGracePeriodSeconds)
                {
                    logger.LogDebug("Node {Node} is Ready but only for {Duration}s, need {Grace}s for unfencing",
                        nodeName, (int)readyDuration.TotalSeconds, opts.NodeReadyGracePeriodSeconds);
                    continue;
                }

                logger.LogInformation("Unfencing node {Node} (Ready for {Duration}s)", nodeName, (int)readyDuration.TotalSeconds);
                await UntaintNodeAsync(node, ct);
                await blacklistManager.RemoveNodeAsync(nodeName, ct);
                metrics.RecordUnfence(nodeName);

                await PublishNodeEventAsync(node, "NodeUnfenced",
                    $"Node {nodeName} unfenced after recovery", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error during unfencing check for node {Node}", nodeName);
            }
        }
    }

    internal async Task<List<V1VolumeAttachment>> FindVolumeAttachmentsForPodAsync(V1Pod pod, CancellationToken ct)
    {
        var nodeName = pod.Spec.NodeName;
        var ns = pod.Metadata.NamespaceProperty;
        var result = new List<V1VolumeAttachment>();

        // Get PVC names from pod volumes
        var pvcNames = pod.Spec.Volumes?
            .Where(v => v.PersistentVolumeClaim != null)
            .Select(v => v.PersistentVolumeClaim.ClaimName)
            .ToList() ?? [];

        if (pvcNames.Count == 0)
            return result;

        // Resolve PVC → PV names
        var pvNames = new List<string>();
        foreach (var pvcName in pvcNames)
        {
            var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
            if (pvc?.Spec.VolumeName != null)
                pvNames.Add(pvc.Spec.VolumeName);
        }

        if (pvNames.Count == 0)
            return result;

        // List all VolumeAttachments and filter by PV name and node
        var allAttachments = await kubernetesClient.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, ct);

        foreach (var va in allAttachments)
        {
            if (va.Spec.NodeName != nodeName)
                continue;

            var pvName = va.Spec.Source?.PersistentVolumeName;
            if (pvName != null && pvNames.Contains(pvName))
                result.Add(va);
        }

        return result;
    }

    private async Task<bool> IsNodeNotReadyAsync(string nodeName, CancellationToken ct)
    {
        var node = await kubernetesClient.GetAsync<V1Node>(nodeName, null, ct);
        return node != null && !IsNodeReady(node);
    }

    internal static bool IsNodeReady(V1Node node)
    {
        var readyCondition = node.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "Ready");
        return readyCondition?.Status == "True";
    }

    internal static bool IsOwnedByStatefulSet(V1Pod pod)
    {
        return pod.Metadata.OwnerReferences?
            .Any(o => o.Kind == "StatefulSet") == true;
    }

    private static DateTimeOffset? GetReadyTransitionTime(V1Node node)
    {
        var readyCondition = node.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "Ready" && c.Status == "True");
        return readyCondition?.LastTransitionTime;
    }

    private async Task PublishEventAsync(V1Pod pod, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        try
        {
            await eventPublisher.PublishAsync(pod, reason, message, type, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to publish event {Reason} for pod {PodName}", reason, pod.Metadata.Name);
        }
    }

    private async Task PublishNodeEventAsync(V1Node node, string reason, string message, CancellationToken ct)
    {
        try
        {
            await eventPublisher.PublishAsync(node, reason, message, ct: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to publish event {Reason} for node {NodeName}", reason, node.Metadata.Name);
        }
    }
}
