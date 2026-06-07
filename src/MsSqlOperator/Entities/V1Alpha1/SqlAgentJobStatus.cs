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
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }

    /// <summary>Targets (instances/AGs) matched by the selector.</summary>
    public int TotalTargets { get; set; }

    /// <summary>Targets the job was successfully configured on this pass.</summary>
    public int ConfiguredTargets { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
