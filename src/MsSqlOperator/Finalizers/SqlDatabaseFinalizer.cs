using Microsoft.Extensions.Logging;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

/// <summary>
/// SqlDatabase deletion. <c>Retain</c> (default): nothing in SQL Server — the database, and its AG
/// membership, stay; the SqlAvailabilityGroupDatabase it managed is released. <c>Delete</c>: the
/// database leaves its AG (the SqlAvailabilityGroupDatabase is deleted, its finalizer removes it),
/// then it is dropped on the primary and on every former secondary — only when this resource owns it.
/// </summary>
public class SqlDatabaseFinalizer(
    ILogger<SqlDatabaseFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlDatabaseManager databases,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    ActuationGateway? gateway = null) : IEntityFinalizer<SqlDatabase>
{
    private ActuationGateway Gateway => gateway ?? ActuationGateway.Default;

    public string Identifier => "mssql-operator.evariops.com/sqldatabase-finalizer";

    public IReadOnlyList<string> LegacyIdentifiers => [];

    public async Task<ReconciliationResult<SqlDatabase>> FinalizeAsync(SqlDatabase entity, CancellationToken ct)
    {
        var spec = entity.Spec;
        var ns = entity.Metadata.NamespaceProperty;
        var managed = (await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct))
            .FirstOrDefault(d => SqlDatabaseController.AnnotationOf(d.Metadata, SqlDatabaseController.ManagedByUidAnnotation) == entity.Metadata.Uid);

        if (spec.DeletionPolicy != "Delete")
        {
            if (managed is not null)
                await kubernetesClient.PatchMetadataAsync(managed, new Dictionary<string, string?>
                {
                    [SqlDatabaseController.ManagedByAnnotation] = null,
                    [SqlDatabaseController.ManagedByUidAnnotation] = null,
                }, ct);
            await eventPublisher.PublishAsync(entity, "DatabaseRetained",
                $"Database '{spec.DatabaseName}' is kept (deletionPolicy Retain)" +
                (managed is null ? "" : $"; SqlAvailabilityGroupDatabase '{managed.Metadata.Name}' keeps it in its availability group"), ct: ct);
            return ReconciliationResult<SqlDatabase>.Success(entity);
        }

        var onAg = spec.AvailabilityGroupRef is { Name.Length: > 0 };
        var targetName = onAg ? spec.AvailabilityGroupRef!.Name : spec.InstanceRef?.Name ?? "";
        var target = (await targetResolver.ResolveAsync(onAg
                ? new TargetSelector { AvailabilityGroups = [targetName] }
                : new TargetSelector { Instances = [targetName] }, ns, ct))
            .FirstOrDefault();
        if (target is null)
        {
            await eventPublisher.PublishAsync(entity, "DatabaseNotDropped",
                $"{(onAg ? "SqlAvailabilityGroup" : "SqlInstance")} '{targetName}' is gone: database '{spec.DatabaseName}' was not dropped", "Warning", ct);
            return ReconciliationResult<SqlDatabase>.Success(entity);
        }

        if (managed is not null)
        {
            // The database leaves the AG first: an AG database cannot be dropped. A
            // SqlAvailabilityGroupDatabase keeps its database in the AG by default: the one this
            // resource manages is told to remove it before it is deleted.
            if (managed.Spec.DeletionPolicy != SqlAvailabilityGroupDatabaseSpec.RemovePolicy)
            {
                managed = await kubernetesClient.PatchAsync(managed,
                    [MsSqlOperator.Operator.Client.JsonPatch.Add("/spec/deletionPolicy", SqlAvailabilityGroupDatabaseSpec.RemovePolicy)], ct);
            }
            if (managed.Metadata.DeletionTimestamp is null)
                await kubernetesClient.DeleteAsync(managed, ct);
            return ReconciliationResult<SqlDatabase>.Failure(entity,
                $"waiting for SqlAvailabilityGroupDatabase '{managed.Metadata.Name}' to remove '{spec.DatabaseName}' from the availability group");
        }

        if (onAg)
        {
            if (await targetResolver.FindPrimaryReplicaAsync(target.Name, ns, target.SaPassword, target.ReplicaCount ?? 3, ct) is not { } primary)
                return ReconciliationResult<SqlDatabase>.Failure(entity, $"availability group '{targetName}' has no primary to drop the database on");
            await using (primary.Connection)
                if (!await DropIfOwnedAsync(entity, primary.Connection, primary.ReplicaName, ct))
                    return ReconciliationResult<SqlDatabase>.Success(entity);

            // The former secondaries keep their copy once the database left the AG: RESTORING, after a
            // few seconds in which it is still ONLINE. Only a RESTORING copy is dropped there; any error
            // on a replica that answers is retried, not left behind (the copy would stay for good).
            foreach (var (host, port, replica) in await TargetConnections.EveryServerAsync(target, kubernetesClient, ct))
            {
                if (replica is null || replica == primary.ReplicaName)
                    continue;
                Microsoft.Data.SqlClient.SqlConnection conn;
                try
                {
                    conn = await connectionFactory.OpenAsync(host, port, target.SaPassword, SqlProfile.Ddl, "master", ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Replica {Replica} unreachable while dropping {Db}", replica, spec.DatabaseName);
                    await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                        $"Replica {replica} is not reachable: its copy of '{spec.DatabaseName}' may remain", "Warning", ct);
                    continue;
                }
                await using (conn)
                {
                    if (await databases.GetDatabaseAsync(conn, spec.DatabaseName, ct) is not { } copy)
                        continue;
                    if (!copy.State.Equals("RESTORING", StringComparison.OrdinalIgnoreCase))
                        return ReconciliationResult<SqlDatabase>.Failure(entity,
                            $"the copy of '{spec.DatabaseName}' on {replica} is {copy.State}: waiting for it to leave the availability group");
                    await Gateway.DropDatabaseAsync(entity, databases, conn, spec.DatabaseName, replica, ActuationIntent.ResourceDeletion, ct);
                }
            }
        }
        else
        {
            await using var conn = await connectionFactory.OpenAsync(target.Host, target.Port, target.SaPassword, SqlProfile.Ddl, "master", ct);
            if (!await DropIfOwnedAsync(entity, conn, target.Name, ct))
                return ReconciliationResult<SqlDatabase>.Success(entity);
        }

        await eventPublisher.PublishAsync(entity, "DatabaseDropped", $"Database '{spec.DatabaseName}' dropped (deletionPolicy Delete)", ct: ct);
        return ReconciliationResult<SqlDatabase>.Success(entity);
    }

    /// <summary>Drops the database when this resource owns it. False when it does not: it is left as
    /// it is, and said so.</summary>
    private async Task<bool> DropIfOwnedAsync(SqlDatabase entity, Microsoft.Data.SqlClient.SqlConnection connection, string server,
        CancellationToken ct)
    {
        var dbName = entity.Spec.DatabaseName;
        if (await databases.GetDatabaseAsync(connection, dbName, ct) is null)
            return true;
        var tag = await databases.GetOwnerTagAsync(connection, dbName, ct);
        if (tag != SqlDatabaseController.OwnerTag(entity))
        {
            await eventPublisher.PublishAsync(entity, "DatabaseNotDropped",
                $"Database '{dbName}' on {server} is not owned by this SqlDatabase ({tag ?? "no owner"}): it was not dropped", "Warning", ct);
            return false;
        }
        await Gateway.DropDatabaseAsync(entity, databases, connection, dbName, server, ActuationIntent.ResourceDeletion, ct);
        return true;
    }
}
