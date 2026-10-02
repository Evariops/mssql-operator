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

    /// <summary>Trace flags the operator turned on, per instance (AG replicas by pod name). Only
    /// these are ever turned off. Kept in the status, not in annotations of the CR
    /// (a full PUT of the user's object drifted from GitOps and raced with its writers).</summary>
    public List<ManagedTraceFlagsStatus>? ManagedTraceFlags { get; set; }

    /// <summary>sp_configure values found before the operator first changed them, per instance
    /// restored when the option leaves the spec, when the instance leaves the selection,
    /// and when the configuration is deleted.</summary>
    public List<OriginalSettingStatus>? OriginalSettings { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}

public class ManagedTraceFlagsStatus
{
    public string Instance { get; set; } = string.Empty;
    public List<int> Flags { get; set; } = [];
}

public class OriginalSettingStatus
{
    public string Instance { get; set; } = string.Empty;
    public string Option { get; set; } = string.Empty;
    public int Value { get; set; }
}
