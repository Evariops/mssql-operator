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

    public List<AgReplicaStatus>? Replicas { get; set; }

    /// <summary>Aggregated summary of SqlAvailabilityGroupDatabase CRDs referencing this AG.</summary>
    public AgDatabaseSummaryStatus? DatabaseSummary { get; set; }

    public List<AgCertificateStatus>? Certificates { get; set; }
    public AgLastFailoverStatus? LastFailover { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
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
