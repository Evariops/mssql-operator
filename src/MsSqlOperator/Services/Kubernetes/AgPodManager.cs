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
    public async Task SetPodRoleLabelAsync(string podName, string ns, string agName, string role, CancellationToken ct = default)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod == null) return;

        pod.Metadata.Labels ??= new Dictionary<string, string>();
        pod.Metadata.Labels["mssql-operator.evariops.com/ag-role"] = role;
        pod.Metadata.Labels["mssql-operator.evariops.com/ag"] = agName;

        await kubernetesClient.UpdateAsync(pod, ct);
        logger.LogInformation("Set pod {Pod} ag-role={Role}", podName, role);
    }

    public async Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod == null) return;

        pod.Metadata.Labels ??= new Dictionary<string, string>();
        pod.Metadata.Labels["mssql-operator.evariops.com/sync-state"] = syncState.ToLowerInvariant().Replace('_', '-');
        pod.Metadata.Labels["mssql-operator.evariops.com/failover-ready"] = failoverReady ? "true" : "false";

        await kubernetesClient.UpdateAsync(pod, ct);
    }

    public async Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod == null) return;

        pod.Metadata.Labels ??= new Dictionary<string, string>();
        pod.Metadata.Labels["mssql-operator.evariops.com/readable"] = readable ? "true" : "false";
        pod.Metadata.Labels["mssql-operator.evariops.com/read-intent-ready"] = readIntentReady ? "true" : "false";
        pod.Metadata.Labels["mssql-operator.evariops.com/listener-ready"] = listenerReady;

        await kubernetesClient.UpdateAsync(pod, ct);
    }

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
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod?.Status?.ContainerStatuses == null) return false;
        return pod.Status.ContainerStatuses.All(c => c.Ready);
    }

    public async Task<List<V1Pod>> GetAgPodsAsync(string stsName, string ns, string agName, CancellationToken ct = default)
    {
        // Get pods by listing all pods with the AG label
        var pods = new List<V1Pod>();
        // KubeOps client doesn't have label selectors; retrieve by ordinal names
        var sts = await kubernetesClient.GetAsync<V1StatefulSet>(stsName, ns, ct);
        if (sts?.Spec.Replicas == null) return pods;

        for (var i = 0; i < sts.Spec.Replicas; i++)
        {
            var pod = await kubernetesClient.GetAsync<V1Pod>($"{stsName}-{i}", ns, ct);
            if (pod != null) pods.Add(pod);
        }
        return pods;
    }
}
