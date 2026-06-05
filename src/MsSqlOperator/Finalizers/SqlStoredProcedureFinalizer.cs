using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlStoredProcedureFinalizer(
    ILogger<SqlStoredProcedureFinalizer> logger,
    ISqlConnectionFactory connectionFactory,
    ISqlStoredProcedureManager storedProcManager,
    ITargetResolver targetResolver) : IEntityFinalizer<SqlStoredProcedure>
{
    public async Task<ReconciliationResult<SqlStoredProcedure>> FinalizeAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlStoredProcedure {Name}", entity.Metadata.Name);

        if (entity.Status?.TargetStatuses == null || entity.Status.TargetStatuses.Count == 0)
            return ReconciliationResult<SqlStoredProcedure>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            try
            {
                Microsoft.Data.SqlClient.SqlConnection? conn;
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null) continue;
                    conn = primary.Value.Connection;
                }
                else
                {
                    conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                }

                await using (conn)
                {
                    var targetStatus = entity.Status.TargetStatuses.FirstOrDefault(t => t.Kind == target.Kind && t.Name == target.Name);
                    if (targetStatus?.DeployedProcedures == null) continue;

                    foreach (var dbProcs in targetStatus.DeployedProcedures)
                    {
                        if (dbProcs.Procedures == null) continue;
                        foreach (var proc in dbProcs.Procedures)
                        {
                            await storedProcManager.DropProcedureAsync(conn, dbProcs.Database, proc.Schema, proc.Name, ct);
                            logger.LogInformation("Dropped procedure [{Schema}].[{Name}] from {Db} on {Target}", proc.Schema, proc.Name, dbProcs.Database, target.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during SqlStoredProcedure finalization for {Name} on {Target}", entity.Metadata.Name, target.Name);
                var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql.operator/force-finalize", out var forceVal) == true
                                    && forceVal == "true";
                if (!forceFinalize)
                    return ReconciliationResult<SqlStoredProcedure>.Failure(entity, $"Finalization failed on {target.Kind}/{target.Name}: {ex.Message}");
            }
        }

        return ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }
}
