using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupDatabaseStatus
{
    /// <summary>Pending | WaitingForAg | Adding | Seeding | Synchronized | Synchronizing
    /// | Suspended | NotSynchronizing | Removing | Failed.</summary>
    public string Phase { get; set; } = Phases.Pending;

    public long ObservedGeneration { get; set; }

    public string? AvailabilityGroupName { get; set; }
    public string? DatabaseName { get; set; }

    /// <summary>Synchronization state per replica.</summary>
    public List<AgDatabaseReplicaState>? ReplicaStates { get; set; }

    /// <summary>Consecutive HADR RESUME attempts that did not bring the database back.
    /// After a few, the operator stops retrying and reports the real state instead of masking it
    /// behind a permanent "Synchronizing".</summary>
    public int ConsecutiveResumeAttempts { get; set; }

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
    // Queue sizes are live telemetry — served by the view API and metrics, not status.
}
