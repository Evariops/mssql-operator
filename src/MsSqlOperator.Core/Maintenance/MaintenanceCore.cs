namespace MsSqlOperator.Core.Maintenance;

/// <summary>What announces that a SQL pod is about to stop.</summary>
public enum SignalKind
{
    /// <summary>An eviction was requested for the pod (webhook annotation, or DisruptionTarget
    /// EvictionByEvictionAPI): kubectl drain, talosctl upgrade, kured, autoscalers.</summary>
    Eviction,
    /// <summary>The pod is being deleted (deletionTimestamp): kubectl delete, a forced drain.</summary>
    Deletion,
    /// <summary>The kubelet terminates the pod (DisruptionTarget TerminationByKubelet): graceful node
    /// shutdown, node pressure.</summary>
    KubeletTermination,
    /// <summary>Another DisruptionTarget reason: taint manager, scheduler preemption.</summary>
    Disruption,
    /// <summary>The node is cordoned (spec.unschedulable).</summary>
    Cordon,
    /// <summary>The node carries a disruption taint (autoscalers, out-of-service).</summary>
    Taint,
    /// <summary>The node announces its shutdown (Ready=False, "node is shutting down").</summary>
    NodeShutdown,
}

/// <summary>Whether the signal concerns the pod alone, or every pod of its node.</summary>
public enum SignalScope { Pod, Node }

public sealed record MaintenanceSignal(SignalKind Kind, SignalScope Scope, string Detail, DateTimeOffset? Since);

/// <summary>A pod as the shell observed it: what can announce its own stop. EvictionRequestedAt is the
/// <c>disruption-requested-at</c> annotation the eviction webhook sets.</summary>
public sealed record PodFacts(
    string Name,
    string? Node,
    DateTimeOffset? DeletionTimestamp,
    string? DisruptionTargetReason,
    DateTimeOffset? DisruptionTargetSince,
    DateTimeOffset? EvictionRequestedAt);

/// <summary>A node as the shell observed it.</summary>
public sealed record NodeFacts(
    string Name,
    bool Unschedulable,
    IReadOnlyList<string> TaintKeys,
    string? ReadyStatus,
    string? ReadyMessage,
    DateTimeOffset? ReadySince);

/// <summary>
/// The signals that a SQL pod is about to stop, as a pure function of the pod and of its
/// node. Each comes with its scope: a pod-scoped signal (an eviction, a deletion) leaves the other
/// pods of the node where they are, which lets a failover target another replica on the same node;
/// a node-scoped signal (cordon, taint, shutdown) concerns every pod of the node.
/// </summary>
public static class MaintenanceCore
{
    /// <summary>Taints that announce the removal of a node, besides the cordon. Extended by
    /// configuration (Operator:NodeMaintenanceTaints).</summary>
    public static readonly IReadOnlyList<string> DefaultDisruptionTaints =
    [
        "karpenter.sh/disrupted",
        "karpenter.sh/disruption",
        "ToBeDeletedByClusterAutoscaler",
        "DeletionCandidateOfClusterAutoscaler",
        "node.kubernetes.io/out-of-service",
    ];

    /// <summary>The cordon is read from spec.unschedulable; its taint is not a second signal.</summary>
    private const string UnschedulableTaint = "node.kubernetes.io/unschedulable";

    public static IReadOnlyList<MaintenanceSignal> Classify(PodFacts pod, NodeFacts? node, IReadOnlyCollection<string> disruptionTaints)
    {
        var signals = new List<MaintenanceSignal>();

        if (pod.EvictionRequestedAt is { } requested)
            signals.Add(new(SignalKind.Eviction, SignalScope.Pod, "eviction requested", requested));

        switch (pod.DisruptionTargetReason)
        {
            case null:
                break;
            case "EvictionByEvictionAPI":
                // Already said by the webhook annotation when the webhook saw the request.
                if (pod.EvictionRequestedAt is null)
                    signals.Add(new(SignalKind.Eviction, SignalScope.Pod, "evicted through the Eviction API", pod.DisruptionTargetSince));
                break;
            case "TerminationByKubelet":
                signals.Add(new(SignalKind.KubeletTermination, SignalScope.Pod, "terminated by the kubelet", pod.DisruptionTargetSince));
                break;
            default:
                signals.Add(new(SignalKind.Disruption, SignalScope.Pod, $"disruption target ({pod.DisruptionTargetReason})", pod.DisruptionTargetSince));
                break;
        }

        if (pod.DeletionTimestamp is { } deleting)
            signals.Add(new(SignalKind.Deletion, SignalScope.Pod, "pod deleted", deleting));

        if (node is null || pod.Node is null || !node.Name.Equals(pod.Node, StringComparison.Ordinal))
            return signals;

        if (node.Unschedulable)
            signals.Add(new(SignalKind.Cordon, SignalScope.Node, $"node {node.Name} cordoned", null));

        foreach (var taint in node.TaintKeys.Where(k => k != UnschedulableTaint && disruptionTaints.Contains(k)).Distinct())
            signals.Add(new(SignalKind.Taint, SignalScope.Node, $"node {node.Name} tainted {taint}", null));

        if (IsShuttingDown(node))
            signals.Add(new(SignalKind.NodeShutdown, SignalScope.Node, $"node {node.Name} is shutting down", node.ReadySince));

        return signals;
    }

    /// <summary>The kubelet's graceful node shutdown reports Ready=False with "node is shutting down".</summary>
    public static bool IsShuttingDown(NodeFacts node)
        => node.ReadyStatus == "False" && node.ReadyMessage?.Contains("shutting down", StringComparison.OrdinalIgnoreCase) == true;
}
