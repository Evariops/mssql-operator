using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlInstanceStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? Version { get; set; }
    public string? Edition { get; set; }
    public string? Endpoint { get; set; }
    public MemoryStatus? Memory { get; set; }
    public List<PodStatus>? PodStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class PodStatus
{
    public string Name { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public string? Role { get; set; }
}
