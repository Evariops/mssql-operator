using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

public class TopologySpreadBuilder : ITopologySpreadBuilder
{
    private const string HostnameKey = "kubernetes.io/hostname";
    private const string ZoneKey = "topology.kubernetes.io/zone";

    public List<V1TopologySpreadConstraint>? Build(
        TopologySpreadSpec? spec, string? pool, IReadOnlyDictionary<string, string> identityLabels)
    {
        if (spec == null) return null;

        var constraints = new List<V1TopologySpreadConstraint>();

        // Self-spread: selector is the resource identity only (stable across in-place upgrades).
        if (spec.Self != null)
            AddRule(constraints, spec.Self, new Dictionary<string, string>(identityLabels));

        // Pool-spread: selector counts pods of every resource sharing the pool label. A pool rule
        // without spec.pool is rejected at admission; defensively skip it here too.
        if (spec.Pool != null && !string.IsNullOrEmpty(pool))
            AddRule(constraints, spec.Pool, new Dictionary<string, string> { [OperatorLabels.Pool] = pool });

        // Raw escape hatch, appended verbatim.
        if (spec.Constraints is { Count: > 0 })
            constraints.AddRange(spec.Constraints);

        return constraints.Count > 0 ? constraints : null;
    }

    private static void AddRule(List<V1TopologySpreadConstraint> list, SpreadRule rule, IDictionary<string, string> selector)
    {
        if (rule.Node is { } node)
            list.Add(MakeConstraint(HostnameKey, node, rule, selector));
        if (rule.Zone is { } zone)
            list.Add(MakeConstraint(ZoneKey, zone, rule, selector));
    }

    private static V1TopologySpreadConstraint MakeConstraint(
        string topologyKey, string mode, SpreadRule rule, IDictionary<string, string> selector)
    {
        var required = mode.Equals("Required", StringComparison.OrdinalIgnoreCase);
        return new V1TopologySpreadConstraint
        {
            TopologyKey = topologyKey,
            MaxSkew = rule.MaxSkew,
            WhenUnsatisfiable = required ? "DoNotSchedule" : "ScheduleAnyway",
            // minDomains is ignored by Kubernetes unless whenUnsatisfiable is DoNotSchedule.
            MinDomains = required ? rule.MinDomains : null,
            LabelSelector = new V1LabelSelector { MatchLabels = selector }
        };
    }
}
