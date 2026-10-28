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

    /// <summary>The data-loss failover (its timestamp) whose divergent replicas were approved to
    /// rejoin. Superseded by <see cref="ResolvedDivergences"/>: no longer read,
    /// removed after a transition release.</summary>
    public DateTime? DataLossResumeApprovedFor { get; set; }

    /// <summary>Divergences of the AG (openDivergences ids) this database has settled: its
    /// rejoin was approved (the approval annotation is consumed into it), or the divergent copy was
    /// re-seeded. Only ids still open on the AG are kept.</summary>
    public List<string>? ResolvedDivergences { get; set; }

    /// <summary>Rounds of RESUME attempts that ended without the database coming back;
    /// at the second, a stuck replica is re-seeded. Reset when nothing is suspended.</summary>
    public int FruitlessResumeRounds { get; set; }

    /// <summary>When each replica was last re-seeded: an automatic re-seed is not repeated
    /// within the hour.</summary>
    public Dictionary<string, DateTime>? ReseededAt { get; set; }

    /// <summary>Seeding attempts of the current re-seed of each replica: a log backup
    /// between the seeding and the join fails it (1412), and it is retried a few times.</summary>
    public Dictionary<string, int>? ReseedAttempts { get; set; }

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
