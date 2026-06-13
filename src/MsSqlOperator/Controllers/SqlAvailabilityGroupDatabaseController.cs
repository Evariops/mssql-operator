using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlAvailabilityGroupDatabase), Verbs = RbacVerb.All)]
public class SqlAvailabilityGroupDatabaseController(
    ILogger<SqlAvailabilityGroupDatabaseController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IEventPublisher eventPublisher) : IEntityController<SqlAvailabilityGroupDatabase>
{
    public async Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> ReconcileAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        logger.LogInformation("Reconciling SqlAvailabilityGroupDatabase {Name} (db={Db}, ag={Ag}, phase={Phase})",
            name, dbName, agName, entity.Status?.Phase ?? "null");

        var phase = entity.Status?.Phase ?? "Pending";

        entity = phase switch
        {
            "Pending" or "WaitingForAg" => await ReconcileWaitingAsync(entity, ct),
            "Adding" or "Seeding" or "Synchronizing" => await ReconcileSyncingAsync(entity, ct),
            "Synchronized" or "NotSynchronizing" or "Suspended" => await ReconcileRunningAsync(entity, ct),
            // Failed is not terminal: re-evaluate from scratch so transient errors
            // (primary unreachable, database briefly absent from DMVs) self-recover.
            "Failed" => await ReconcileWaitingAsync(entity, ct),
            _ => entity
        };

        // Periodic requeue: sync state changes without touching the CR (seeding progress,
        // suspension, replica loss) and must be re-observed on a timer.
        var requeue = (entity.Status?.Phase ?? phase) switch
        {
            "Adding" or "Seeding" or "Synchronizing" => TimeSpan.FromSeconds(15),
            "Failed" => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromSeconds(30)
        };
        return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity, requeue);
    }

    public Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> DeletedAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAvailabilityGroupDatabase {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity));
    }

    // ── Phase: Pending / WaitingForAg ─────────────────────

    private async Task<SqlAvailabilityGroupDatabase> ReconcileWaitingAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        // 1. Resolve the AG reference
        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
        if (ag == null)
        {
            logger.LogInformation("AG {Ag} not found, waiting", agName);
            return await UpdatePhase(entity, "WaitingForAg", "AgNotFound",
                $"SqlAvailabilityGroup '{agName}' not found in namespace '{ns}'", ct);
        }

        if (ag.Status?.Phase != "Running")
        {
            logger.LogInformation("AG {Ag} is in phase {Phase}, waiting for Running", agName, ag.Status?.Phase);
            await eventPublisher.PublishAsync(entity, "DatabaseWaitingForAg",
                $"Waiting for AG {agName} to reach Running phase (current: {ag.Status?.Phase})", ct: ct);
            return await UpdatePhase(entity, "WaitingForAg", "AgNotReady",
                $"AG '{agName}' is in phase '{ag.Status?.Phase}', waiting for Running", ct);
        }

        // 2. Connect to primary and validate the database
        var primaryReplica = ag.Status.PrimaryReplica;
        if (primaryReplica == null)
            return await UpdatePhase(entity, "WaitingForAg", "NoPrimary", "AG has no primary replica", ct);

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
            return await UpdatePhase(entity, "Failed", "SaPasswordNotFound", "Cannot resolve SA password from AG", ct);

        var headlessService = $"{agName}-headless";
        var primaryFqdn = $"{primaryReplica}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct);

            // 3. Check if database exists on primary
            var dbExists = await agManager.DatabaseExistsOnPrimaryAsync(conn, dbName, ct);
            if (!dbExists)
            {
                await eventPublisher.PublishAsync(entity, "DatabaseNotFound",
                    $"Database '{dbName}' does not exist on primary {primaryReplica}", "Warning", ct);
                return await UpdatePhase(entity, "Failed", "DatabaseNotFound",
                    $"Database '{dbName}' does not exist on primary replica", ct);
            }

            // 4. Check if database is already in an AG
            var existingAg = await agManager.GetDatabaseAgNameAsync(conn, dbName, ct);
            if (existingAg != null)
            {
                if (existingAg.Equals(agName, StringComparison.OrdinalIgnoreCase))
                {
                    // Already in this AG — skip to monitoring
                    logger.LogInformation("Database {Db} already in AG {Ag}", dbName, agName);
                    return await ReconcileRunningAsync(
                        await UpdatePhase(entity, "Synchronized", "AlreadyInAg",
                            $"Database already in AG {agName}", ct), ct);
                }

                await eventPublisher.PublishAsync(entity, "DatabaseBelongsToOtherAg",
                    $"Database '{dbName}' belongs to AG '{existingAg}'", "Warning", ct);
                return await UpdatePhase(entity, "Failed", "DatabaseBelongsToOtherAg",
                    $"Database '{dbName}' already belongs to AG '{existingAg}'", ct);
            }

            // 5. Prepare and add database to AG
            entity = await UpdatePhase(entity, "Adding", "AddingDatabase",
                $"Adding database '{dbName}' to AG '{agName}'", ct);

            var backupPath = $"/var/opt/mssql/backup/{dbName}_ag_init.bak";
            await agManager.PrepareDatabaseForAgAsync(conn, dbName, backupPath, ct);
            await agManager.AddDatabaseToAgAsync(conn, agName, dbName, ct);

            // The backup can run for minutes — the resourceVersion captured at entry
            // is stale by now; re-read before the final status write to avoid a 409 that would
            // replay the whole reconcile (and its backup decision).
            var fresh = await kubernetesClient.GetAsync<SqlAvailabilityGroupDatabase>(entity.Metadata.Name, ns, ct);
            if (fresh != null)
            {
                fresh.Status = entity.Status;
                entity = fresh;
            }

            logger.LogInformation("Added database {Db} to AG {Ag}", dbName, agName);
            await eventPublisher.PublishAsync(entity, "DatabaseAddedToAg",
                $"Database '{dbName}' added to AG '{agName}'", ct: ct);

            var seedingPhase = entity.Spec.SeedingMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
                ? "Seeding" : "Synchronizing";

            return await UpdatePhase(entity, seedingPhase, "DatabaseAdded",
                $"Database added, {seedingPhase.ToLowerInvariant()} in progress", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add database {Db} to AG {Ag}", dbName, agName);
            return await UpdatePhase(entity, "Failed", "AddDatabaseFailed",
                $"Failed to add database: {ex.Message}", ct);
        }
    }

    // ── Phase: Adding / Seeding / Synchronizing ───────────

    private async Task<SqlAvailabilityGroupDatabase> ReconcileSyncingAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        // Delegate to the running reconciler which monitors sync state
        return await ReconcileRunningAsync(entity, ct);
    }

    // ── Phase: Synchronized / Suspended / NotSynchronizing ─

    private async Task<SqlAvailabilityGroupDatabase> ReconcileRunningAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
        if (ag?.Status?.PrimaryReplica == null)
            return await UpdatePhase(entity, "WaitingForAg", "NoPrimary", "AG primary not available", ct);

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
            return await UpdatePhase(entity, "Failed", "SaPasswordNotFound",
                "Cannot resolve SA password from AG spec", ct);

        var headlessService = $"{agName}-headless";
        var primaryFqdn = $"{ag.Status.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct);

            // Get database states across replicas
            var dbStates = await agManager.GetDatabaseStatesAsync(conn, agName, ct);
            var thisDbStates = dbStates.Where(d => d.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (thisDbStates.Count == 0)
            {
                // Database not in AG anymore
                return await UpdatePhase(entity, "Failed", "DatabaseNotInAg",
                    $"Database '{dbName}' is no longer in AG '{agName}'", ct);
            }

            // Update replica states in status
            entity.Status ??= new SqlAvailabilityGroupDatabaseStatus();
            entity.Status.AvailabilityGroupName = agName;
            entity.Status.DatabaseName = dbName;
            entity.Status.ReplicaStates = thisDbStates.Select(d => new AgDatabaseReplicaState
            {
                Replica = d.ReplicaName,
                Role = d.Role,
                SynchronizationState = d.SynchronizationState,
                IsFailoverReady = d.IsFailoverReady
            }).ToList();

            // Determine phase from sync states
            var allSynchronized = thisDbStates.All(d =>
                d.SynchronizationState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase));
            var anySuspended = thisDbStates.Any(d =>
                d.SynchronizationState.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase));
            var anyNotSynchronizing = thisDbStates.Any(d =>
                d.SynchronizationState.Equals("NOT SYNCHRONIZING", StringComparison.OrdinalIgnoreCase) ||
                d.SynchronizationState.Equals("NOT_SYNCHRONIZING", StringComparison.OrdinalIgnoreCase));

            // Handle data movement suspension request
            if (entity.Spec.DataMovement?.Suspended == true && !anySuspended)
            {
                await agManager.SuspendDataMovementAsync(conn, dbName, ct);
                return await UpdatePhase(entity, "Suspended", "DataMovementSuspended",
                    "Data movement suspended by spec", ct);
            }

            if (entity.Spec.DataMovement?.Suspended == false && anySuspended)
            {
                // Only resume if allowed
                if (entity.Spec.DataMovement?.RequireManualResumeAfterDataLoss != true)
                {
                    await agManager.ResumeDataMovementAsync(conn, dbName, ct);
                    return await UpdatePhase(entity, "Synchronizing", "DataMovementResumed",
                        "Data movement resumed", ct);
                }
            }

            // Self-heal a database stuck NOT SYNCHRONIZING (e.g. after a transient disconnect):
            // HADR RESUME restarts synchronization. Gated by RequireManualResumeAfterDataLoss so we
            // never silently resume after a forced failover where divergence is possible.
            if (anyNotSynchronizing
                && entity.Spec.DataMovement?.RequireManualResumeAfterDataLoss != true
                && entity.Spec.DataMovement?.Suspended != true)
            {
                try
                {
                    await agManager.ResumeDataMovementAsync(conn, dbName, ct);
                    await eventPublisher.PublishAsync(entity, "ResynchronizationAttempted",
                        $"Database '{dbName}' was NOT SYNCHRONIZING; issued HADR RESUME to restart synchronization", "Warning", ct);
                    return await UpdatePhase(entity, "Synchronizing", "ResumeAttempted",
                        $"Attempting to resynchronize '{dbName}' via HADR RESUME", ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "HADR RESUME for {Db} failed; manual re-seed may be required", dbName);
                }
            }

            string newPhase;
            string reason;
            string message;

            if (anySuspended)
            {
                newPhase = "Suspended";
                reason = "DataMovementSuspended";
                message = $"Database '{dbName}' has suspended data movement on one or more replicas";
            }
            else if (anyNotSynchronizing)
            {
                newPhase = "NotSynchronizing";
                reason = "NotSynchronizing";
                message = $"Database '{dbName}' is not synchronizing on one or more replicas";
            }
            else if (allSynchronized)
            {
                newPhase = "Synchronized";
                reason = "DatabaseSynchronized";
                message = $"Database '{dbName}' synchronized across all replicas";
            }
            else
            {
                newPhase = "Synchronizing";
                reason = "SynchronizationInProgress";
                message = $"Database '{dbName}' synchronization in progress";
            }

            return await UpdatePhase(entity, newPhase, reason, message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check sync state for {Db} in AG {Ag}", dbName, agName);
            return entity; // Will be requeued
        }
    }

    // ── Helpers ───────────────────────────────────────────

    private async Task<SqlAvailabilityGroupDatabase> UpdatePhase(
        SqlAvailabilityGroupDatabase entity, string phase, string reason, string message, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupDatabaseStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.AvailabilityGroupName = entity.Spec.AvailabilityGroupRef.Name;
        entity.Status.DatabaseName = entity.Spec.DatabaseName;

        // Build conditions (AgReady, DatabaseExists, Ready) via ConditionOps so LastTransitionTime only
        // moves on an actual status transition — keeping the status byte-stable across no-op reconciles
        // so the status-write dedup can break the self-reconcile loop.
        var conditions = entity.Status.Conditions ?? [];

        var agReady = phase is not ("Pending" or "WaitingForAg");
        ConditionOps.Set(conditions, "AgReady", agReady ? "True" : "False",
            agReady ? "AgRunning" : reason,
            agReady ? $"AG '{entity.Spec.AvailabilityGroupRef.Name}' is running" : message);

        var dbExists = !(phase is "Pending" or "WaitingForAg") && reason != "DatabaseNotFound";
        ConditionOps.Set(conditions, "DatabaseExists", dbExists ? "True" : "False",
            dbExists ? "DatabaseFound" : reason,
            dbExists ? $"Database '{entity.Spec.DatabaseName}' exists on primary" : message);

        ConditionOps.Set(conditions, "Ready", phase == "Synchronized" ? "True" : "False", reason, message);

        entity.Status.Conditions = conditions;
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private async Task<string?> ResolveSaPasswordFromAgAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        // Null only on 404; transport errors propagate (runtime backoff).
        return await SecretValues.TryGetAsync(kubernetesClient, ag.Spec.SaPasswordSecretRef, ns, ct);
    }
}
