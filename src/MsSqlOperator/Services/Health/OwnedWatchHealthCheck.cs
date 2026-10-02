using Microsoft.Extensions.Diagnostics.HealthChecks;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// Readiness gate on the owned-resource watch. If any child watch has gone silent for
/// longer than 2× its server-side rotation timeout, the stream is a zombie and drift on owned
/// resources (StatefulSet/Service/ConfigMap/…) is only repaired by the slower periodic resync. The replica
/// is then marked NotReady — which restarts nothing: it leaves the webhook and view-API
/// endpoints, and the condition shows on the pod for the operator's own alerting. A non-leader
/// replica (where the watch does not run) always reports healthy.
/// </summary>
public sealed class OwnedWatchHealthCheck(OwnedResourceWatcher watcher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(watcher.IsHealthy()
            ? HealthCheckResult.Healthy("Owned-resource watch active")
            : HealthCheckResult.Unhealthy("An owned-resource watch is stale (no events beyond 2× the rotation timeout)"));
}
