using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Kubernetes;

public interface IAgPodManager
{
    /// <summary>Update the ag-role label on a pod (primary/secondary/resolving).</summary>
    Task SetPodRoleLabelAsync(string podName, string ns, string agName, string role, CancellationToken ct = default);

    /// <summary>Update sync-state and failover-ready labels on a pod.</summary>
    Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default);

    /// <summary>
    /// Set the read-routing labels driving the RO service selector and the R/W listener:
    /// <c>readable</c> (the secondary accepts read connections), <c>read-intent-ready</c>
    /// (readable AND connected AND synchronized), and <c>listener-ready</c>
    /// (<c>writer</c>|<c>reader</c>|<c>none</c>) which gates the writer endpoint.
    /// </summary>
    Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default);

    /// <summary>Fence the old primary by force-deleting its pod (gracePeriod=0). Returns true
    /// only when the pod is confirmed gone from the API server — callers must treat
    /// false as NOT fenced and never proceed to a data-loss failover.</summary>
    Task<bool> FenceByDeletePodAsync(string podName, string ns, CancellationToken ct = default);

    /// <summary>Fence by patching the StatefulSet replicas to exclude the target ordinal.</summary>
    Task FenceByScaleToZeroAsync(string stsName, string ns, int targetOrdinal, int currentReplicas, CancellationToken ct = default);

    /// <summary>Check if a specific pod is running and ready.</summary>
    Task<bool> IsPodReadyAsync(string podName, string ns, CancellationToken ct = default);

    /// <summary>Get all pods for a StatefulSet.</summary>
    Task<List<V1Pod>> GetAgPodsAsync(string stsName, string ns, string agName, CancellationToken ct = default);
}

public class AgPodManager(
    IKubernetesClient kubernetesClient,
    ILogger<AgPodManager> logger) : IAgPodManager
{
    /// <summary>Labels are applied with a no-op comparison + merge patch limited to
    /// metadata.labels — no GET+PUT full replace, no 409s against the kubelet on the path
    /// that drives the R/W listener.</summary>
    private async Task ApplyLabelsAsync(string podName, string ns, Dictionary<string, string> desired, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod == null) return;

        var current = pod.Metadata.Labels ?? new Dictionary<string, string>();
        if (desired.All(kv => current.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return; // already converged — zero API writes in steady state

        await kubernetesClient.PatchPodLabelsAsync(podName, ns, desired, ct);
    }

    public async Task SetPodRoleLabelAsync(string podName, string ns, string agName, string role, CancellationToken ct = default)
    {
        await ApplyLabelsAsync(podName, ns, new Dictionary<string, string>
        {
            ["mssql-operator.evariops.com/ag-role"] = role,
            ["mssql-operator.evariops.com/ag"] = agName,
        }, ct);
        logger.LogDebug("Ensured pod {Pod} ag-role={Role}", podName, role);
    }

    public Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default)
        => ApplyLabelsAsync(podName, ns, new Dictionary<string, string>
        {
            ["mssql-operator.evariops.com/sync-state"] = syncState.ToLowerInvariant().Replace('_', '-'),
            ["mssql-operator.evariops.com/failover-ready"] = failoverReady ? "true" : "false",
        }, ct);

    public Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default)
        => ApplyLabelsAsync(podName, ns, new Dictionary<string, string>
        {
            ["mssql-operator.evariops.com/readable"] = readable ? "true" : "false",
            ["mssql-operator.evariops.com/read-intent-ready"] = readIntentReady ? "true" : "false",
            ["mssql-operator.evariops.com/listener-ready"] = listenerReady,
        }, ct);

    public async Task<bool> FenceByDeletePodAsync(string podName, string ns, CancellationToken ct = default)
    {
        logger.LogWarning("FENCING: Force-deleting pod {Pod} in namespace {Ns}", podName, ns);
        await kubernetesClient.ForceDeletePodAsync(podName, ns, ct);

        // Fencing is acquired only when the pod is actually gone — a graceful delete
        // of a pod on a dead node leaves it Terminating (and serving writes) indefinitely.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct) is null)
                return true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        var gone = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct) is null;
        if (!gone)
            logger.LogWarning("FENCING: pod {Pod} still present after force deletion; fencing NOT confirmed", podName);
        return gone;
    }

    public async Task FenceByScaleToZeroAsync(string stsName, string ns, int targetOrdinal, int currentReplicas, CancellationToken ct = default)
    {
        // Scale the StatefulSet down to remove the target pod
        // This only works if the target is the highest ordinal
        // For non-highest ordinals, we delete the pod directly
        if (targetOrdinal == currentReplicas - 1)
        {
            var sts = await kubernetesClient.GetAsync<V1StatefulSet>(stsName, ns, ct);
            if (sts != null)
            {
                logger.LogWarning("FENCING: Scaling StatefulSet {Sts} from {Current} to {New}", stsName, currentReplicas, currentReplicas - 1);
                sts.Spec.Replicas = currentReplicas - 1;
                await kubernetesClient.UpdateAsync(sts, ct);
            }
        }
        else
        {
            // Can't selectively remove a middle ordinal via scale-down
            // Fall back to pod deletion
            var podName = $"{stsName}-{targetOrdinal}";
            await FenceByDeletePodAsync(podName, ns, ct);
        }
    }

    public async Task<bool> IsPodReadyAsync(string podName, string ns, CancellationToken ct = default)
    {
        // The pod Ready condition is the real signal — All() over an empty
        // containerStatuses returned true for pods that had not even started containers.
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        return pod?.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
    }

    public async Task<List<V1Pod>> GetAgPodsAsync(string stsName, string ns, string agName, CancellationToken ct = default)
    {
        // One LIST by AG label instead of N GETs by ordinal — also sees pods that
        // outlive a scale-down (the ordinal loop had a blind spot there).
        var pods = await kubernetesClient.ListAsync<V1Pod>(ns, $"mssql-operator.evariops.com/ag={agName}", ct);
        return [.. pods];
    }
}
