namespace MsSqlOperator.Entities.Common;

public class ResourceRequirements
{
    public ResourceQuantity? Requests { get; set; }
    public ResourceQuantity? Limits { get; set; }
    public bool CpuPinning { get; set; }
}

public class ResourceQuantity
{
    public string? Cpu { get; set; }
    public string? Memory { get; set; }

    /// <summary>The pod writes to ephemeral storage (SMK temp files, errorlog
    /// without a dedicated volume); without a request, disk-pressure eviction is unmanaged.</summary>
    public string? EphemeralStorage { get; set; }
}

/// <summary>Shutdown behaviour of the SQL pod.</summary>
public class ShutdownSpec
{
    /// <summary>terminationGracePeriodSeconds — SQL Server needs time to checkpoint; the k8s
    /// default of 30s SIGKILLs sqlservr mid-checkpoint and forces a long crash-recovery.</summary>
    public long GracePeriodSeconds { get; set; } = 180;

    /// <summary>Availability groups only: how long the preStop hook of the primary waits for
    /// the planned failover the operator starts when the pod stops (deletion, node shutdown), while a
    /// synchronous secondary is synchronized. 0 never waits. Kept 45 s under the grace period. The
    /// operator only arms the wait when it will fail over (NodeMaintenance in Enforce).</summary>
    public int PrimaryHandoffSeconds { get; set; } = 30;
}

/// <summary>Opt-in PodDisruptionBudget for standalone instances.</summary>
public class PodDisruptionBudgetSpec
{
    public bool Enabled { get; set; }
}
