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
    IKubernetes? rawClient = null) : BackgroundService
{
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

    private async Task RunControlLoopAsync(CancellationToken ct)
    {
        var opts = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.ControlLoopPeriodSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await RunLoopAsync(ct);
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
    }

    private bool IsDryRun => string.Equals(options.Value.FencingDriver, "none", StringComparison.OrdinalIgnoreCase);

    private int _loopCount;

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
        var pods = await kubernetesClient.ListAsync<V1Pod>((string?)null, opts.PodSelector, ct) ?? [];
        var agPods = await kubernetesClient.ListAsync<V1Pod>((string?)null, opts.PodSelectorAg, ct) ?? [];

        var terminatingPods = pods.Concat(agPods)
            .Where(p => p.Metadata.DeletionTimestamp.HasValue)
            .DistinctBy(p => p.Metadata.Uid)
            .ToList();

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
    /// </summary>
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
            logger.LogDebug(ex, "Could not resolve the owner of pod {PodName} to check the pause annotation", pod.Metadata.Name);
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

        // Safety: check not already blacklisted
        if (await blacklistManager.IsBlacklistedAsync(nodeName, ct))
        {
            logger.LogDebug("Node {Node} already blacklisted, force-deleting pod {PodName}", nodeName, podName);
            // The NoExecute taint is what actually keeps the fenced node empty, and it was
            // applied once, on a full-object Update with no 409 retry. If that single attempt lost the
            // race with another writer, the node stayed blacklisted but untainted forever — the
            // scheduler happily placed the replacement pod back on the dying node. Re-assert it here so
            // "blacklisted ⇒ tainted" is an invariant reconciled on every pass, not a one-shot effect.
            if (!IsDryRun)
                await TaintNodeAsync(nodeName, ct);
            await FenceAndDeletePodAsync(pod, ct);
            return false; // Node already counted
        }

        logger.LogWarning("Fencing node {Node} due to terminating pod {PodName} (terminating for {Duration}s)",
            nodeName, podName, (int)terminatingDuration.TotalSeconds);

        // Step 1: Add node to blacklist
        await blacklistManager.AddNodeAsync(nodeName, ct);
        metrics.RecordFence(nodeName);

        // A NoExecute taint is what actually keeps the fenced node empty — pod
        // deletion alone is undone by the StatefulSet within seconds, and the blacklist by
        // itself prevents nothing at the scheduler level. Removed on unfencing.
        if (!IsDryRun)
            await TaintNodeAsync(nodeName, ct);

        // Step 2: Detach volumes then force-delete pod (strict order per spec)
        await FenceAndDeletePodAsync(pod, ct);

        // In 'none' (log-only) mode no actual fencing happens — say so rather than emitting a
        // misleading "fenced" event.
        var dryRun = string.Equals(options.Value.FencingDriver, "none", StringComparison.OrdinalIgnoreCase);
        await PublishEventAsync(pod,
            dryRun ? "NodeFenceSimulated" : "NodeFenced",
            dryRun
                ? $"[DRY-RUN] Node {nodeName} would be fenced (fencingDriver=none); no VolumeAttachments were deleted"
                : $"Node {nodeName} fenced (VolumeAttachments deleted)",
            "Warning", ct);

        return true;
    }

    /// <summary>VolumeAttachments we already asked the CSI driver to delete: re-issuing
    /// the delete and re-waiting 30s for each of them on every 5-second loop turn is what made one
    /// uncooperative node stall the fencing of every other pod for minutes.
    ///
    /// Keyed by UID, not by name. VolumeAttachment names are DETERMINISTIC
    /// (csi-&lt;hash of driver+PV+node&gt;), so after a fence→unfence→re-fence cycle of the same node
    /// the recreated attachment reused the name of the one we detached last time — the entry left
    /// behind by an unconfirmed first fencing then filtered the new detach out forever, and the
    /// second fencing never completed. A recreated attachment always has a fresh UID.</summary>
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
                await fencingDriver.DetachVolumesAsync(pod, toDetach, ct);
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
        await fencingDriver.ForceDeletePodAsync(pod, ct);
        metrics.RecordForceDelete(podName);

        await PublishEventAsync(pod, "ForcedDeletion",
            $"Pod {podName} force-deleted after volume detachment", "Warning", ct);

        logger.LogWarning("Pod {PodName} force-deleted from fenced node {Node}", podName, nodeName);
    }

    internal const string FencedTaintKey = "mssql-operator.evariops.com/fenced";

    /// <summary>Applies the NoExecute fencing taint, retrying on a concurrent write:
    /// a lost 409 used to leave a blacklisted node untainted forever, so the scheduler put the
    /// replacement pod straight back onto the dying node.</summary>
    private async Task TaintNodeAsync(string nodeName, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var node = await kubernetesClient.GetAsync<V1Node>(nodeName, null, ct);
            if (node == null) return;

            node.Spec ??= new V1NodeSpec();
            node.Spec.Taints ??= [];
            if (node.Spec.Taints.Any(t => t.Key == FencedTaintKey)) return; // already tainted

            node.Spec.Taints.Add(new V1Taint { Key = FencedTaintKey, Value = "true", Effect = "NoExecute" });
            try
            {
                await kubernetesClient.UpdateAsync(node, ct);
                logger.LogWarning("Tainted node {Node} with {Taint}=true:NoExecute", nodeName, FencedTaintKey);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.LogWarning(ex, "Could not taint node {Node} (attempt {Attempt}/4); retrying with a fresh object",
                    nodeName, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), timeProvider, ct);
            }
        }

        logger.LogError(
            "Node {Node} is blacklisted but could NOT be tainted with {Taint}: the scheduler may place a replacement pod " +
            "back on it. Taint it manually.", nodeName, FencedTaintKey);
    }

    private async Task UntaintNodeAsync(V1Node node, CancellationToken ct)
    {
        var taints = node.Spec?.Taints;
        if (taints == null) return;
        var removed = taints.Where(t => t.Key == FencedTaintKey).ToList();
        if (removed.Count == 0) return;
        foreach (var t in removed) taints.Remove(t);
        await kubernetesClient.UpdateAsync(node, ct);
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
