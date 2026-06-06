using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? Edition { get; set; }
    public string ClusterType { get; set; } = "None";
    public string? PrimaryReplica { get; set; }
    public string? PrimaryEndpoint { get; set; }
    public string? ReadOnlyEndpoint { get; set; }

    public MemoryStatus? Memory { get; set; }

    public List<AgReplicaStatus>? Replicas { get; set; }

    /// <summary>Aggregated summary of SqlAvailabilityGroupDatabase CRDs referencing this AG.</summary>
    public AgDatabaseSummaryStatus? DatabaseSummary { get; set; }

    public List<AgCertificateStatus>? Certificates { get; set; }
    public AgLastFailoverStatus? LastFailover { get; set; }

    /// <summary>Progress of an in-flight AG-aware rolling update.</summary>
    public AgRollingUpdateStatus? RollingUpdate { get; set; }

    /// <summary>
    /// Signature of the read-only routing topology last applied to SQL Server (primary + readable
    /// secondaries + port). Lets the controller re-apply routing only when the topology actually
    /// changes (failover, replica add/remove, readability flip) instead of every cycle.
    /// </summary>
    public string? ReadOnlyRoutingSignature { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}

public class AgRollingUpdateStatus
{
    /// <summary>Idle|Planning|UpdatingSecondary|FailingOverForUpdate|UpdatingPrimary|Completing|Failed.</summary>
    public string Phase { get; set; } = "Idle";

    /// <summary>The pod currently being updated (deleted and awaited), if any.</summary>
    public string? CurrentPod { get; set; }

    /// <summary>Fine-grained step for the current pod: DeletingPod|WaitingHealthy|Pausing.</summary>
    public string? Step { get; set; }

    public int TotalPods { get; set; }
    public int UpdatedPods { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CurrentStepStartedAt { get; set; }

    /// <summary>Earliest time the next pod may be started (pauseBetweenPodsSeconds gate).</summary>
    public DateTime? NextActionAfter { get; set; }

    /// <summary>The StatefulSet updateRevision this rollout converges toward.</summary>
    public string? TargetRevision { get; set; }
}

public class AgReplicaStatus
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Unknown";
    public string AvailabilityMode { get; set; } = "Unknown";
    public string SynchronizationState { get; set; } = "Unknown";
    public string ConnectionState { get; set; } = "Unknown";
    public bool IsFailoverReady { get; set; }
    public bool Readable { get; set; }
    public string ListenerReady { get; set; } = "none";
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>Total log send queue across this replica's databases (KB). Replication lag indicator.</summary>
    public long LogSendQueueSize { get; set; }

    /// <summary>Total redo queue across this replica's databases (KB). Read-staleness indicator.</summary>
    public long RedoQueueSize { get; set; }
}

public class AgCertificateStatus
{
    public string? ReplicaName { get; set; }
    public string? Name { get; set; }
    public string? Thumbprint { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public List<string>? TrustedByReplicas { get; set; }
}

public class AgLastFailoverStatus
{
    public DateTime? Timestamp { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Reason { get; set; }
    public bool DataLoss { get; set; }
}

public class AgDatabaseSummaryStatus
{
    public int Total { get; set; }
    public int Synchronized { get; set; }
    public int Synchronizing { get; set; }
    public int Suspended { get; set; }
    public int Failed { get; set; }
    public List<AgDatabaseSummaryEntry>? Databases { get; set; }
}

public class AgDatabaseSummaryEntry
{
    public string Name { get; set; } = string.Empty;
    public string Phase { get; set; } = "Unknown";
}
