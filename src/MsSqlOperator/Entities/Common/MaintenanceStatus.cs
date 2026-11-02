namespace MsSqlOperator.Entities.Common;

/// <summary>The pods of the resource that a node maintenance is about to stop, as last
/// observed. Absent when none is; nothing is written while no signal is seen.</summary>
public class MaintenanceStatus
{
    /// <summary>Off, Shadow or Enforce: the NodeMaintenance switch in force for the resource. In Shadow,
    /// the signals and decisions are published and nothing is acted on.</summary>
    public string Mode { get; set; } = string.Empty;

    public List<MaintenancePodStatus> Pods { get; set; } = [];
}

public class MaintenancePodStatus
{
    public string Pod { get; set; } = string.Empty;
    public string? Node { get; set; }

    /// <summary>Kinds of the signals seen: Eviction, Deletion, KubeletTermination, Disruption, Cordon,
    /// Taint, NodeShutdown.</summary>
    public List<string> Signals { get; set; } = [];

    /// <summary>When the operator first saw a signal for this pod.</summary>
    public DateTime? Since { get; set; }
}
