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
