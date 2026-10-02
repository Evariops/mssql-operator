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
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher) : IEntityFinalizer<SqlAgentJob>
{
    public string Identifier => "mssql-operator.evariops.com/sqlagentjob-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqlagentjobfinalizer.finalizers.mssql-operator.evariops.com"];

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
                if (target.IsAvailabilityGroup)
                {
                    // The job lives in the msdb of every replica (it is not replicated).
                    for (var i = 0; i < (target.ReplicaCount ?? 3); i++)
                    {
                        var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);
                        await using var conn = await connectionFactory.CreateConnectionAsync(host, SqlEndpoints.ContainerPort, target.SaPassword, "master", ct);
                        await DeleteIfOperatorManagedAsync(entity, conn, job.Name, target, ct);
                    }
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    if (!await DeleteIfOperatorManagedAsync(entity, conn, job.Name, target, ct))
                        continue;
                }

                logger.LogInformation("Deleted SQL Agent job {JobName} on {Kind}/{Target}", job.Name, target.Kind, target.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error deleting SQL Agent job {JobName} on {Kind}/{Target}", job.Name, target.Kind, target.Name);
                var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/force-finalize", out var forceVal) == true
                                    && forceVal == "true";
                if (!forceFinalize)
                    return ReconciliationResult<SqlAgentJob>.Failure(entity, $"Finalization failed on {target.Kind}/{target.Name}: {ex.Message}");
            }
        }

        return ReconciliationResult<SqlAgentJob>.Success(entity);
    }

    /// <summary>
    /// Drops the msdb job only when the operator created it: the marker
    /// <c>[mssqlop:…]</c> in the description is the proof of ownership. A job of the same name that a
    /// DBA created — the case of a CR that never converged and was deleted to be re-made — must
    /// survive the deletion of the CR, not be silently destroyed with it.
    /// </summary>
    private async Task<bool> DeleteIfOperatorManagedAsync(
        SqlAgentJob entity, Microsoft.Data.SqlClient.SqlConnection conn, string jobName,
        Entities.Common.ResolvedTarget target, CancellationToken ct)
    {
        var description = await sqlAgentManager.GetJobDescriptionAsync(conn, jobName, ct);
        if (description is null)
            return true; // nothing there: deleting is a no-op (and DeleteJobAsync is idempotent)

        if (!Controllers.SqlAgentJobController.IsOperatorManaged(description))
        {
            logger.LogWarning(
                "SQL Agent job {JobName} on {Kind}/{Target} was not created by the operator (no [mssqlop:…] marker); leaving it in msdb",
                jobName, target.Kind, target.Name);
            await eventPublisher.PublishAsync(entity, "CleanupSkippedNotOwned",
                $"SQL Agent job '{jobName}' on {target.Kind}/{target.Name} was not created by the operator; it is left untouched.",
                ct: ct);
            return false;
        }

        await sqlAgentManager.DeleteJobAsync(conn, jobName, ct);
        return true;
    }
}
