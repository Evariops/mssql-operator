using k8s;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// Readiness check that confirms the operator can still reach the Kubernetes API server. Without
/// it, <c>/readyz</c> returns 200 even when the operator is disconnected and inert — it would keep
/// receiving traffic and never be restarted. Calls <c>GET /version</c>, which needs no RBAC.
/// </summary>
public sealed class ApiServerHealthCheck(IKubernetes client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await client.Version.GetCodeAsync(cancellationToken);
            return HealthCheckResult.Healthy($"API server reachable (git {version.GitVersion})");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Kubernetes API server unreachable", ex);
        }
    }
}
