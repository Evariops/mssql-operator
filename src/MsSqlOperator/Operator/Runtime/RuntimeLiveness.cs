using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// The liveness of the reconcile runtimes. A runtime whose workers are all stuck — a
/// connection to a frozen server, a deadlock in a controller — kept the process alive and /healthz
/// green, so the kubelet never restarted it while no resource of that kind was reconciled any more.
/// A kind with work waiting longer than its threshold (max of 3× the resync and 2× the pass timeout:
/// longer than any healthy pass) fails the liveness probe.
/// </summary>
public sealed class RuntimeLiveness
{
    private readonly ConcurrentDictionary<string, (Func<double> OldestPendingSeconds, TimeSpan Threshold)> _kinds = new();

    public void Register(string kind, Func<double> oldestPendingSeconds, TimeSpan threshold)
        => _kinds[kind] = (oldestPendingSeconds, threshold);

    /// <summary>The kinds stuck right now, with how long their oldest key has waited.</summary>
    public IReadOnlyList<(string Kind, double WaitingSeconds, TimeSpan Threshold)> Stuck()
        => _kinds.Select(k => (k.Key, Waiting: k.Value.OldestPendingSeconds(), k.Value.Threshold))
            .Where(k => k.Waiting > k.Threshold.TotalSeconds)
            .ToList();
}

/// <summary>healthz fails while a runtime is stuck.</summary>
public sealed class RuntimeLivenessHealthCheck(RuntimeLiveness liveness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stuck = liveness.Stuck();
        return Task.FromResult(stuck.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(string.Join("; ", stuck.Select(s =>
                $"{s.Kind}: work waiting {s.WaitingSeconds:F0}s with nothing dequeued (threshold {s.Threshold.TotalSeconds:F0}s)"))));
    }
}
