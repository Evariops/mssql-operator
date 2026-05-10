using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Kubernetes;

public interface IAgPodManager
{
    /// <summary>Update the ag-role label on a pod (primary/secondary/resolving).</summary>
    Task SetPodRoleLabelAsync(string podName, string ns, string agName, string role, CancellationToken ct = default);

    /// <summary>Update sync-state and failover-ready labels on a pod.</summary>
    Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default);

    /// <summary>Fence the old primary by scaling its ordinal to 0 (delete the pod).</summary>
    Task FenceByDeletePodAsync(string podName, string ns, CancellationToken ct = default);

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
        pod.Metadata.Labels["mssql.operator/ag-role"] = role;
        pod.Metadata.Labels["mssql.operator/ag"] = agName;

        await kubernetesClient.UpdateAsync(pod, ct);
        logger.LogInformation("Set pod {Pod} ag-role={Role}", podName, role);
    }

    public async Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod == null) return;

        pod.Metadata.Labels ??= new Dictionary<string, string>();
        pod.Metadata.Labels["mssql.operator/sync-state"] = syncState.ToLowerInvariant().Replace('_', '-');
        pod.Metadata.Labels["mssql.operator/failover-ready"] = failoverReady ? "true" : "false";

        await kubernetesClient.UpdateAsync(pod, ct);
    }

    public async Task FenceByDeletePodAsync(string podName, string ns, CancellationToken ct = default)
    {
        logger.LogWarning("FENCING: Deleting pod {Pod} in namespace {Ns}", podName, ns);
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod != null)
        {
            await kubernetesClient.DeleteAsync(pod, ct);
        }
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
