using System.Diagnostics;
using k8s.Models;
using KubeOps.KubernetesClient;
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
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation("Failover Breakout Controller started (period={Period}s, threshold={Threshold}s, driver={Driver})",
            opts.ControlLoopPeriodSeconds, opts.TerminatingThresholdSeconds, opts.FencingDriver);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.ControlLoopPeriodSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in failover breakout control loop");
            }
        }

        logger.LogInformation("Failover Breakout Controller stopped");
    }

    internal async Task RunLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        await ProcessTerminatingPodsAsync(ct);
        await ProcessUnfencingAsync(ct);

        sw.Stop();
        metrics.RecordLoopDuration(sw.Elapsed.TotalSeconds);
    }

    internal async Task ProcessTerminatingPodsAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var pods = await kubernetesClient.ListAsync<V1Pod>((string?)null, opts.PodSelector, ct);

        var terminatingPods = pods
            .Where(p => p.Metadata.DeletionTimestamp.HasValue)
            .ToList();

        if (terminatingPods.Count == 0)
            return;

        // Enforce max concurrent fencing
        var blacklisted = await blacklistManager.GetBlacklistedNodesAsync(ct);
        var currentFencedCount = blacklisted.Count;

        foreach (var pod in terminatingPods)
        {
            if (currentFencedCount >= opts.MaxConcurrentFencing)
            {
                logger.LogWarning("Max concurrent fencing limit ({Max}) reached, skipping remaining pods", opts.MaxConcurrentFencing);
                break;
            }

            try
            {
                var fenced = await ProcessPodAsync(pod, ct);
                if (fenced) currentFencedCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing terminating pod {PodName}", pod.Metadata.Name);
            }
        }
    }

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

        // Safety: check terminating duration
        var terminatingDuration = timeProvider.GetUtcNow() - pod.Metadata.DeletionTimestamp!.Value.ToUniversalTime();
        if (terminatingDuration.TotalSeconds < opts.TerminatingThresholdSeconds)
        {
            logger.LogDebug("Pod {PodName} has been Terminating for {Duration}s, below threshold {Threshold}s",
                podName, (int)terminatingDuration.TotalSeconds, opts.TerminatingThresholdSeconds);
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
            await FenceAndDeletePodAsync(pod, ct);
            return false; // Node already counted
        }

        logger.LogWarning("Fencing node {Node} due to terminating pod {PodName} (terminating for {Duration}s)",
            nodeName, podName, (int)terminatingDuration.TotalSeconds);

        // Step 1: Add node to blacklist
        await blacklistManager.AddNodeAsync(nodeName, ct);
        metrics.RecordFence(nodeName);
        metrics.NodeFenced();

        // Step 2: Detach volumes then force-delete pod (strict order per spec)
        await FenceAndDeletePodAsync(pod, ct);

        await PublishEventAsync(pod, "NodeFenced",
            $"Node {nodeName} fenced (VolumeAttachments deleted)", "Warning", ct);

        return true;
    }

    private async Task FenceAndDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;
        var nodeName = pod.Spec.NodeName;

        // Step 1: Find and detach VolumeAttachments
        var volumeAttachments = await FindVolumeAttachmentsForPodAsync(pod, ct);

        if (volumeAttachments.Count > 0)
        {
            await fencingDriver.DetachVolumesAsync(pod, volumeAttachments, ct);

            foreach (var va in volumeAttachments)
                metrics.RecordVolumeDetach(podName);

            await PublishEventAsync(pod, "VolumeDetached",
                $"Detached {volumeAttachments.Count} volume(s) for pod {podName} from node {nodeName}", ct: ct);

            // Verify detachment
            var remaining = await FindVolumeAttachmentsForPodAsync(pod, ct);
            if (remaining.Count > 0)
            {
                logger.LogWarning("Post-detach verification: {Count} VolumeAttachments still present for pod {PodName}",
                    remaining.Count, podName);
            }
        }

        // Step 2: Force-delete the pod
        await fencingDriver.ForceDeletePodAsync(pod, ct);
        metrics.RecordForceDelete(podName);

        await PublishEventAsync(pod, "ForcedDeletion",
            $"Pod {podName} force-deleted after volume detachment", "Warning", ct);

        logger.LogWarning("Pod {PodName} force-deleted from fenced node {Node}", podName, nodeName);
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
                    metrics.NodeUnfenced();
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
                await blacklistManager.RemoveNodeAsync(nodeName, ct);
                metrics.RecordUnfence(nodeName);
                metrics.NodeUnfenced();

                await PublishNodeEventAsync(node, "NodeUnfenced",
                    $"Node {nodeName} unfenced after recovery", ct);
            }
            catch (Exception ex)
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
        catch (Exception ex)
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish event {Reason} for node {NodeName}", reason, node.Metadata.Name);
        }
    }
}
