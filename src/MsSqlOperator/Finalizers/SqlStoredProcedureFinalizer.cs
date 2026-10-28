using MsSqlOperator.Operator.Abstractions;
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
    ISqlServerManager sqlServerManager,
    ITargetResolver targetResolver) : IEntityFinalizer<SqlStoredProcedure>
{
    public string Identifier => "mssql-operator.evariops.com/sqlstoredprocedure-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqlstoredprocedurefinalizer.finalizers.mssql-operator.evariops.com"];

    public async Task<ReconciliationResult<SqlStoredProcedure>> FinalizeAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlStoredProcedure {Name}", entity.Metadata.Name);

        // Drop exactly what this resource deployed, identified by the deployment marker it
        // stamped on each procedure — in every database, since the filter may have changed since. It
        // used to drop every procedure with the same schema and name in every database the filter
        // currently matched, including ones this resource never deployed (a DBA's, another CR's).
        // GitHub-sourced procedures are covered too: their names no longer need to be re-fetched.
        var ns = entity.Metadata.NamespaceProperty;
        var owner = $"{ns}/{entity.Metadata.Name}";
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            try
            {
                Microsoft.Data.SqlClient.SqlConnection? conn;
                if (target.IsAvailabilityGroup)
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    // An AG without a primary used to be skipped, and the finalizer released with
                    // the procedures still deployed. It is retried, unless force-finalize says otherwise.
                    conn = primary?.Connection
                           ?? throw new InvalidOperationException($"no primary replica of {target.Kind}/{target.Name} answers");
                }
                else
                {
                    conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                }

                await using (conn)
                {
                    foreach (var db in await sqlServerManager.GetDatabaseNamesAsync(conn, ct))
                    {
                        foreach (var state in await storedProcManager.GetDeploymentStatesAsync(conn, db, ct))
                        {
                            if (ProcedureMarker.Parse(state.Marker)?.Owner != owner)
                                continue;
                            await storedProcManager.DropProcedureAsync(conn, db, state.Schema, state.Name, ct);
                            logger.LogInformation("Dropped procedure [{Schema}].[{Name}] from {Db} on {Target}", state.Schema, state.Name, db, target.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during SqlStoredProcedure finalization for {Name} on {Target}", entity.Metadata.Name, target.Name);
                var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/force-finalize", out var forceVal) == true
                                    && forceVal == "true";
                if (!forceFinalize)
                    return ReconciliationResult<SqlStoredProcedure>.Failure(entity, $"Finalization failed on {target.Kind}/{target.Name}: {ex.Message}");
            }
        }

        return ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }
}
