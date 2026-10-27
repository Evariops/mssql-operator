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
    /// <remarks><paramref name="readOnlyTarget"/>: whether the read-only listener selects the pod
    /// a read-intent-ready secondary when not given.</remarks>
    Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default,
        bool? readOnlyTarget = null);

    /// <summary>
    /// Role, sync and read-routing labels of a replica in one go: one GET and at most one
    /// patch per replica and pass, instead of three of each.
    /// </summary>
    async Task SetReplicaLabelsAsync(string podName, string ns, string agName, string role, string syncState, bool failoverReady,
        bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default, bool? readOnlyTarget = null)
    {
        await SetPodRoleLabelAsync(podName, ns, agName, role, ct);
        await SetPodSyncLabelsAsync(podName, ns, syncState, failoverReady, ct);
        await SetReadabilityLabelsAsync(podName, ns, readable, readIntentReady, listenerReady, ct, readOnlyTarget);
    }

    // Fencing lives in IReplicaFencer. The former FenceByDeletePodAsync here
    // force-deleted the Pod object with gracePeriod=0 and reported "fenced" as soon as the object
    // left etcd — which, on a partitioned node, proves nothing: the kubelet is exactly the component
    // that is unreachable, so sqlservr keeps running with its disk attached. Worse, the object
    // vanishing hid the pod from the breakout controller (it only scans pods with a deletionTimestamp),
    // disabling the one mechanism that would have detached its volumes.

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

    public Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default,
        bool? readOnlyTarget = null)
        => ApplyLabelsAsync(podName, ns, new Dictionary<string, string>
        {
            ["mssql-operator.evariops.com/readable"] = readable ? "true" : "false",
            ["mssql-operator.evariops.com/read-intent-ready"] = readIntentReady ? "true" : "false",
            ["mssql-operator.evariops.com/listener-ready"] = listenerReady,
            [ReadOnlyTargetLabel] = readOnlyTarget ?? readIntentReady ? "true" : "false",
        }, ct);

    /// <summary>The read-only listener selects the pods carrying it: read-intent-ready
    /// secondaries, or the primary when none is and the listener falls back to it.</summary>
    public const string ReadOnlyTargetLabel = "mssql-operator.evariops.com/read-only-target";

    public Task SetReplicaLabelsAsync(string podName, string ns, string agName, string role, string syncState, bool failoverReady,
        bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default, bool? readOnlyTarget = null)
        => ApplyLabelsAsync(podName, ns, new Dictionary<string, string>
        {
            [ReadOnlyTargetLabel] = readOnlyTarget ?? readIntentReady ? "true" : "false",
            ["mssql-operator.evariops.com/ag-role"] = role,
            ["mssql-operator.evariops.com/ag"] = agName,
            ["mssql-operator.evariops.com/sync-state"] = syncState.ToLowerInvariant().Replace('_', '-'),
            ["mssql-operator.evariops.com/failover-ready"] = failoverReady ? "true" : "false",
            ["mssql-operator.evariops.com/readable"] = readable ? "true" : "false",
            ["mssql-operator.evariops.com/read-intent-ready"] = readIntentReady ? "true" : "false",
            ["mssql-operator.evariops.com/listener-ready"] = listenerReady,
        }, ct);

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
