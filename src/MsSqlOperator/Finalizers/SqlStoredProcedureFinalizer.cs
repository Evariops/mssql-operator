using MsSqlOperator.Operator.Abstractions;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlStoredProcedureFinalizer(
    ILogger<SqlStoredProcedureFinalizer> logger,
    ISqlConnectionFactory connectionFactory,
    ISqlStoredProcedureManager storedProcManager,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    ITargetResolver targetResolver) : IEntityFinalizer<SqlStoredProcedure>
{
    public string Identifier => "mssql-operator.evariops.com/sqlstoredprocedure-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqlstoredprocedurefinalizer.finalizers.mssql-operator.evariops.com"];

    public async Task<ReconciliationResult<SqlStoredProcedure>> FinalizeAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlStoredProcedure {Name}", entity.Metadata.Name);

        // The slim status no longer records what was deployed; recompute the procedures to drop from
        // the spec. Inline procedures carry their (schema, name); GitHub-sourced procedures are not
        // fetched during finalization (best-effort cleanup, stateless by design).
        var procs = (entity.Spec.Inline ?? [])
            .Select(p => (Schema: string.IsNullOrWhiteSpace(p.Schema) ? "dbo" : p.Schema, p.Name))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
        if (procs.Count == 0)
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
                    var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(conn, ct);
                    var databases = databaseFilter.Apply(allDatabases, entity.Spec.Databases, entity.Spec.DatabaseFilter);

                    foreach (var db in databases)
                    {
                        foreach (var (schema, name) in procs)
                        {
                            await storedProcManager.DropProcedureAsync(conn, db, schema, name, ct);
                            logger.LogInformation("Dropped procedure [{Schema}].[{Name}] from {Db} on {Target}", schema, name, db, target.Name);
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
