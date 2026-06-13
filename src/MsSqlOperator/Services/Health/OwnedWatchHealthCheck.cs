using Microsoft.Extensions.Diagnostics.HealthChecks;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// Readiness gate on the owned-resource watch. If any child watch has gone silent for
/// longer than 2× its server-side rotation timeout, the stream is a zombie and drift on owned
/// resources (StatefulSet/Service/ConfigMap/…) would only be repaired by the slow periodic resync — so
/// the replica is marked NotReady and Kubernetes restarts it. A non-leader replica (where the
/// watch does not run) always reports healthy.
/// </summary>
public sealed class OwnedWatchHealthCheck(OwnedResourceWatcher watcher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(watcher.IsHealthy()
            ? HealthCheckResult.Healthy("Owned-resource watch active")
            : HealthCheckResult.Unhealthy("An owned-resource watch is stale (no events beyond 2× the rotation timeout)"));
}
