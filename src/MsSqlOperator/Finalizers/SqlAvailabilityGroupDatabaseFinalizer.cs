using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

public class SqlAvailabilityGroupDatabaseFinalizer(
    ILogger<SqlAvailabilityGroupDatabaseFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IEventPublisher eventPublisher) : IEntityFinalizer<SqlAvailabilityGroupDatabase>
{
    public string Identifier => "mssql-operator.evariops.com/sqlavailabilitygroupdatabase-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqlavailabilitygroupdatabasefinalizer.finalizers.mssql-operator.evariops.com"];

    public async Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> FinalizeAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        logger.LogInformation("Finalizing SqlAvailabilityGroupDatabase {Name} (db={Db}, ag={Ag})",
            entity.Metadata.Name, dbName, agName);

        // Check for force-cleanup annotation
        var forceCleanup = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/finalizer-policy", out var policy) == true
                           && policy == "force-kubernetes-cleanup";

        if (forceCleanup)
        {
            logger.LogWarning("Force cleanup requested for {Name}, skipping SQL cleanup", entity.Metadata.Name);
            await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                $"Database '{dbName}' may still be in AG '{agName}' (force cleanup)", "Warning", ct);
            return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
        }

        // Another CR may own the same database in the same AG — the duplicate that the
        // uniqueness tie-break parked in phase Failed (DuplicateDatabase). Deleting that duplicate is
        // the natural cleanup, and it used to run ALTER AVAILABILITY GROUP … REMOVE DATABASE on the
        // live, replicated database: every secondary copy went RESTORING (no redundancy left, and a
        // failover at that moment loses the database), then the surviving CR re-added it and triggered
        // a full re-seed. The database is only removed from the AG if THIS CR is the one managing it.
        var owner = await FindOwningSiblingAsync(entity, ns, agName, dbName, ct);
        if (owner != null)
        {
            logger.LogWarning(
                "SqlAvailabilityGroupDatabase {Name}: database {Db} in AG {Ag} is managed by {Owner}; leaving it in the AG",
                entity.Metadata.Name, dbName, agName, owner);
            await eventPublisher.PublishAsync(entity, "RemovalSkippedNotOwner",
                $"Database '{dbName}' stays in AG '{agName}': it is managed by SqlAvailabilityGroupDatabase '{owner}'. " +
                "Only the resource that owns it can remove it.", ct: ct);
            return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
        }

        // The AG itself is going away — deleted with its databases, or with the namespace,
        // whose controller deletes the pods at the same time, so the primary never answers again.
        // Waiting for it kept this finalizer, the AG's (which waits for its databases) and the
        // namespace in Terminating for good. Removing a database from an AG being destroyed protects
        // nothing: the database and its AG membership stay on the retained volumes.
        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
        if (ag is null || ag.Metadata.DeletionTimestamp is not null)
        {
            logger.LogInformation("SqlAvailabilityGroupDatabase {Name}: AG {Ag} is being deleted; database {Db} is left as is",
                entity.Metadata.Name, agName, dbName);
            await eventPublisher.PublishAsync(entity, "AgRemovalSkipped",
                $"Availability group '{agName}' is being deleted: database '{dbName}' was not removed from it.", ct: ct);
            return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
        }

        // Removing the database while the primary moves would act on a replica that is about
        // to stop being the primary; the finalizer waits for the topology to settle.
        if (AgTopology.ChangeInProgress(ag) is { } change)
        {
            entity = await UpdateStatus(entity, Phases.Removing, "DeletionBlocked", "True",
                $"{change}: the database is removed from the AG once it ends.", ct);
            throw new InvalidOperationException($"{change}; removal of database '{dbName}' deferred");
        }

        // Set phase to Removing
        entity = await UpdateStatus(entity, Phases.Removing, "DeletionBlocked", "False",
            "Removing database from AG", ct);

        if (ag.Status?.PrimaryReplica == null)
        {
            logger.LogWarning("Cannot finalize {Name}: AG {Ag} primary not available — keeping finalizer",
                entity.Metadata.Name, agName);
            await eventPublisher.PublishAsync(entity, "DeletionBlocked",
                $"Cannot remove database from AG: primary is not reachable. Finalizer kept.", "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Removing, "DeletionBlocked", "True",
                "Primary is not reachable. Use annotation mssql-operator.evariops.com/finalizer-policy: force-kubernetes-cleanup to bypass.", ct);
            // Keep finalizer — requeue. Throwing makes KubeOps requeue the entity.
            throw new InvalidOperationException($"AG '{agName}' primary not available, blocking finalization");
        }

        // An API error propagates (the finalizer is retried) instead of reading as "no password".
        var saPassword = await SecretValues.TryGetAsync(kubernetesClient, ag.Spec.SaPasswordSecretRef, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("Cannot resolve SA password for AG {Ag} — keeping finalizer", agName);
            entity = await UpdateStatus(entity, Phases.Removing, "DeletionBlocked", "True",
                "Cannot resolve SA password from AG", ct);
            throw new InvalidOperationException($"SA password for AG '{agName}' not resolvable, blocking finalization");
        }

        var headlessService = $"{agName}-headless";
        var primaryFqdn = $"{ag.Status!.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.OpenAsync(primaryFqdn, SqlEndpoints.ContainerPort, saPassword, SqlProfile.Hadr, "master", ct);

            // Check if DB is actually in the AG before trying to remove
            var existingAg = await agManager.GetDatabaseAgNameAsync(conn, dbName, ct);
            if (existingAg != null && existingAg.Equals(agName, StringComparison.OrdinalIgnoreCase))
            {
                await agManager.RemoveDatabaseFromAgAsync(conn, agName, dbName, ct);
                logger.LogInformation("Removed database {Db} from AG {Ag}", dbName, agName);
                await eventPublisher.PublishAsync(entity, "DatabaseRemovedFromAg",
                    $"Database '{dbName}' removed from AG '{agName}'. Database NOT dropped.", ct: ct);
            }
            else
            {
                logger.LogInformation("Database {Db} not in AG {Ag}, skipping removal", dbName, agName);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to remove database {Db} from AG {Ag} during finalization — keeping finalizer",
                dbName, agName);
            await eventPublisher.PublishAsync(entity, "DatabaseRemovalFailed",
                $"Failed to remove database '{dbName}' from AG: {ex.Message}. Finalizer kept.", "Warning", ct);
            entity = await UpdateStatus(entity, Phases.Removing, "DeletionBlocked", "True",
                $"SQL error during removal: {ex.Message}", ct);
            // Block finalization on SQL errors — keep finalizer
            throw;
        }

        logger.LogInformation("SqlAvailabilityGroupDatabase {Name} finalized", entity.Metadata.Name);
        return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
    }

    /// <summary>
    /// Name of another, live SqlAvailabilityGroupDatabase managing the same database in the same AG
    /// or null. The winner of the uniqueness tie-break is the CR that is NOT in phase
    /// Failed/DuplicateDatabase — i.e. the one actually driving the database.
    /// </summary>
    private async Task<string?> FindOwningSiblingAsync(
        SqlAvailabilityGroupDatabase entity, string ns, string agName, string dbName, CancellationToken ct)
    {
        // Fail-closed on a listing error: not being able to prove sole ownership means not removing
        // the database from a production availability group.
        var siblings = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct);

        var owner = siblings.FirstOrDefault(s =>
            s.Metadata.Uid != entity.Metadata.Uid
            && s.Metadata.DeletionTimestamp is null
            && s.Spec.AvailabilityGroupRef.Name == agName
            && s.Spec.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase));

        return owner?.Metadata.Name;
    }

    private async Task<SqlAvailabilityGroupDatabase> UpdateStatus(
        SqlAvailabilityGroupDatabase entity, string phase, string conditionType, string conditionStatus,
        string message, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupDatabaseStatus();
        entity.Status.Phase = phase;

        // Standard transition-aware upsert — the home-grown version rewrote
        // LastTransitionTime on every pass, churning the status during a blocked deletion.
        ConditionOps.Set(entity.Status.Conditions ??= [], conditionType, conditionStatus, conditionType, message);

        try
        {
            return await kubernetesClient.UpdateStatusAsync(entity, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not persist finalization status for {Name}", entity.Metadata.Name);
            return entity;
        }
    }
}
