using k8s.Models;

namespace MsSqlOperator.Entities.Common;

/// <summary>
/// Topology spread &amp; cross-instance placement. Sibling of <see cref="AntiAffinitySpec"/>:
/// anti-affinity <i>separates</i> pods, spread <i>balances</i> them (skew-bounded). Translated to the
/// native <c>V1PodSpec.TopologySpreadConstraints</c> (plus a pod anti-affinity term for
/// <see cref="PoolAntiAffinity"/>).
/// </summary>
public class TopologySpreadSpec
{
    /// <summary>Spread THIS resource's own pods (e.g. AG replicas) across the topology. Selector is
    /// the resource identity label only (stable across in-place version upgrades).</summary>
    public SpreadRule? Self { get; set; }

    /// <summary>Spread the pods of ALL resources sharing <c>spec.pool</c> across the topology.
    /// Requires <c>spec.pool</c> to be set.</summary>
    public SpreadRule? Pool { get; set; }

    /// <summary>Strict cross-pool separation on the hostname (pod anti-affinity, not spread): no two
    /// pool members on the same node. "Required" | "Preferred". Requires <c>spec.pool</c>.</summary>
    public string? PoolAntiAffinity { get; set; }

    /// <summary>Escape hatch: raw native constraints appended verbatim (minDomains, matchLabelKeys,
    /// nodeAffinityPolicy, nodeTaintsPolicy, ...).</summary>
    public List<V1TopologySpreadConstraint>? Constraints { get; set; }

    /// <summary>
    /// AG default: prefer spreading replicas across both nodes and zones.
    /// Soft (<c>Preferred</c>) so single-node / single-zone clusters still schedule.
    /// </summary>
    public static TopologySpreadSpec DefaultSelfSpread => new()
    {
        Self = new SpreadRule { Node = "Preferred", Zone = "Preferred" }
    };
}

/// <summary>One spread intent, expanded into 0..2 native constraints (hostname and/or zone).</summary>
public class SpreadRule
{
    /// <summary>Spread across <c>kubernetes.io/hostname</c>. "Required" (DoNotSchedule) |
    /// "Preferred" (ScheduleAnyway) | null (off).</summary>
    public string? Node { get; set; }

    /// <summary>Spread across <c>topology.kubernetes.io/zone</c>. "Required" | "Preferred" | null.</summary>
    public string? Zone { get; set; }

    /// <summary>Maximum allowed difference in pod count between topology domains. Default 1.</summary>
    public int MaxSkew { get; set; } = 1;

    /// <summary>Minimum number of domains that must be occupied. Only honoured with a Required rule.</summary>
    public int? MinDomains { get; set; }
}
