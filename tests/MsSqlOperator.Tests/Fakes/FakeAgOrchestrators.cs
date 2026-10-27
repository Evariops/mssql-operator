using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IAgHealthChecker"/>: returns a configurable report.</summary>
public sealed class FakeAgHealthChecker : IAgHealthChecker
{
    public AgHealthReport Report { get; set; } = new(false, null, [], [], [], [], []);

    public Task<AgHealthReport> CheckHealthAsync(SqlAvailabilityGroup ag, string saPassword, CancellationToken ct = default)
        => Task.FromResult(Report);
}

/// <summary>Hand-written fake for <see cref="IAgFailoverOrchestrator"/>.</summary>
public sealed class FakeAgFailoverOrchestrator : IAgFailoverOrchestrator
{
    public FailoverOutcome Outcome { get; set; } = FailoverOutcome.NoAction;

    /// <summary>What the failover does to the status (a promotion records LastFailover).</summary>
    public Action<SqlAvailabilityGroup>? OnReconcile { get; set; }

    public Task<FailoverOutcome> ReconcileFailoverAsync(SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, CancellationToken ct)
    {
        OnReconcile?.Invoke(ag);
        return Task.FromResult(Outcome);
    }
}

/// <summary>Hand-written fake for <see cref="IAgRollingUpdateOrchestrator"/>.</summary>
public sealed class FakeAgRollingUpdateOrchestrator : IAgRollingUpdateOrchestrator
{
    public RollingUpdateOutcome Outcome { get; set; } = RollingUpdateOutcome.Idle;

    public Task<RollingUpdateOutcome> ReconcileAsync(SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, CancellationToken ct)
        => Task.FromResult(Outcome);
}

/// <summary>Hand-written fake for <see cref="MsSqlOperator.Services.Kubernetes.IVolumeAutoGrower"/>.</summary>
public sealed class FakeVolumeAutoGrower : MsSqlOperator.Services.Kubernetes.IVolumeAutoGrower
{
    /// <summary>Why a volume cannot grow; null when every volume has room.</summary>
    public string? Blocked { get; set; }

    public Task<string?> ReconcileAsync(SqlAvailabilityGroup ag, IEnumerable<string> replicas, string saPassword, CancellationToken ct)
        => Task.FromResult(Blocked);

    /// <summary>Replicas whose backup volume was asked to grow.</summary>
    public readonly List<string> BackupGrowthCalls = [];

    public Task<string> GrowBackupVolumeAsync(SqlAvailabilityGroup ag, string replica, CancellationToken ct)
    {
        BackupGrowthCalls.Add(replica);
        return Task.FromResult($"its volume was grown (backup-{replica} from 10Gi to 15Gi)");
    }
}
