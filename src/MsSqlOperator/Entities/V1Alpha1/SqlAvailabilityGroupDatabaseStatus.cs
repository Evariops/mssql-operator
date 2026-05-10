using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupDatabaseStatus
{
    /// <summary>Pending | WaitingForAg | Adding | Seeding | Synchronized | Synchronizing
    /// | Suspended | NotSynchronizing | Removing | Failed.</summary>
    public string Phase { get; set; } = "Pending";

    public long ObservedGeneration { get; set; }

    public string? AvailabilityGroupName { get; set; }
    public string? DatabaseName { get; set; }

    /// <summary>Synchronization state per replica.</summary>
    public List<AgDatabaseReplicaState>? ReplicaStates { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}

public class AgDatabaseReplicaState
{
    public string Replica { get; set; } = string.Empty;

    /// <summary>Primary | Secondary.</summary>
    public string Role { get; set; } = "Unknown";

    /// <summary>Synchronized | Synchronizing | NotSynchronizing | Suspended.</summary>
    public string SynchronizationState { get; set; } = "Unknown";

    public bool IsFailoverReady { get; set; }
    public long LogSendQueueSize { get; set; }
    public long RedoQueueSize { get; set; }
}
