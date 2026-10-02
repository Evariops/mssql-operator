using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// Slim, etcd-light status: convergence signal + O(1) roll-up scalars only. The per-target
/// array (unbounded by target count, and holding live <c>msdb</c> run data — last/next run — that
/// changes constantly) is gone. Live run state is served by the aggregated APIService
/// (<c>sqlagentjobruns</c>); configuration occurrences are Events; counts are metrics.
/// </summary>
public class SqlAgentJobStatus
{
    /// <summary>The msdb job name last applied: when the spec name differs, the
    /// controller deletes the old job before creating the new one — no orphans.</summary>
    public string? AppliedJobName { get; set; }

    public string Phase { get; set; } = Phases.Pending;
    public long ObservedGeneration { get; set; }

    /// <summary>Targets (instances/AGs) matched by the selector.</summary>
    public int TotalTargets { get; set; }

    /// <summary>Targets the job was successfully configured on this pass.</summary>
    public int ConfiguredTargets { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
