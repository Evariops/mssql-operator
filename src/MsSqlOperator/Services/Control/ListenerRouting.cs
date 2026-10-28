using k8s.Models;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// At safety level 2 in Enforce, the read-write listener selects the primary by its pod
/// name. A label is written on the pod and follows it; a recreated old primary carrying a stale
/// <c>ag-role=primary</c> label got traffic back. A pod name survives the recreation of the pod: the
/// selector is rewritten only by an election, a switchover or an adoption, and the old primary is no
/// longer selected. Below level 2, the label routing stays (migration).
/// </summary>
public static class ListenerRouting
{
    public const string PodNameLabel = "statefulset.kubernetes.io/pod-name";

    public static Dictionary<string, string> Selector(SqlAvailabilityGroup ag, IReadOnlyDictionary<string, string> labels, SafetyMode? level2)
    {
        if (level2 == SafetyMode.Enforce && ag.Status?.PrimaryReplica is { Length: > 0 } primary)
            return new Dictionary<string, string>(labels) { [PodNameLabel] = primary };
        return new Dictionary<string, string>(labels)
        {
            ["mssql-operator.evariops.com/ag-role"] = "primary",
            ["mssql-operator.evariops.com/listener-ready"] = "writer",
        };
    }

    /// <summary>Routes the listener to <paramref name="primary"/> when it selects another pod
    /// (an election, a switchover or an adoption): one write, only then.</summary>
    public static async Task<bool> RouteAsync(IKubernetesClient client, SqlAvailabilityGroup ag, string primary, CancellationToken ct)
    {
        var service = await client.GetAsync<V1Service>($"{ag.Metadata.Name}-listener", ag.Metadata.NamespaceProperty, ct);
        if (service?.Spec?.Selector is not { } selector || selector.TryGetValue(PodNameLabel, out var routed) && routed == primary)
            return false;
        var next = new Dictionary<string, string>(selector) { [PodNameLabel] = primary };
        next.Remove("mssql-operator.evariops.com/ag-role");
        next.Remove("mssql-operator.evariops.com/listener-ready");
        await client.PatchAsync(service, [JsonPatch.Replace("/spec/selector", next)], ct);
        return true;
    }
}
