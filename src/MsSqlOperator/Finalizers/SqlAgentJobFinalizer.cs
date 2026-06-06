using MsSqlOperator.Operator.Abstractions;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlAgentJobFinalizer(
    ILogger<SqlAgentJobFinalizer> logger,
    ISqlConnectionFactory connectionFactory,
    ISqlAgentManager sqlAgentManager,
    ITargetResolver targetResolver) : IEntityFinalizer<SqlAgentJob>
{
    public async Task<ReconciliationResult<SqlAgentJob>> FinalizeAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlAgentJob {Name}", entity.Metadata.Name);

        var job = entity.Spec.Job;
        if (job == null) return ReconciliationResult<SqlAgentJob>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            try
            {
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null) continue;
                    await using var conn = primary.Value.Connection;
                    await sqlAgentManager.DeleteJobAsync(conn, job.Name, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    await sqlAgentManager.DeleteJobAsync(conn, job.Name, ct);
                }

                logger.LogInformation("Deleted SQL Agent job {JobName} on {Kind}/{Target}", job.Name, target.Kind, target.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error deleting SQL Agent job {JobName} on {Kind}/{Target}", job.Name, target.Kind, target.Name);
                var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql.operator/force-finalize", out var forceVal) == true
                                    && forceVal == "true";
                if (!forceFinalize)
                    return ReconciliationResult<SqlAgentJob>.Failure(entity, $"Finalization failed on {target.Kind}/{target.Name}: {ex.Message}");
            }
        }

        return ReconciliationResult<SqlAgentJob>.Success(entity);
    }
}
