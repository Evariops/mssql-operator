using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// Slim, etcd-light status: convergence signal + O(1) roll-up scalars only. The per-instance
/// <c>appliedTo[]</c> array (unbounded by the number of matched instances) is gone — matched instances
/// are emitted as Events and counted via metrics.
/// </summary>
public class SqlConfigurationStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }

    /// <summary>Instances matched by the selector.</summary>
    public int MatchedInstances { get; set; }

    /// <summary>Matched instances the configuration was successfully applied to this pass.</summary>
    public int AppliedInstances { get; set; }

    public DateTime? LastAppliedTime { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
