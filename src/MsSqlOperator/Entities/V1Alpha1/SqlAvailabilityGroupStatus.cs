using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? PrimaryReplica { get; set; }
    public List<string>? SynchronizedReplicas { get; set; }
    public List<string>? AsyncReplicas { get; set; }
    public string? ListenerEndpoint { get; set; }
    public string? ReadOnlyEndpoint { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}
