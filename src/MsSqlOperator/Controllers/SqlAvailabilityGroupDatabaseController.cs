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
    IEventPublisher eventPublisher,
    TimeProvider? timeProvider = null) : IEntityController<SqlAvailabilityGroupDatabase>
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> ReconcileAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        logger.LogDebug("Reconciling SqlAvailabilityGroupDatabase {Name} (db={Db}, ag={Ag}, phase={Phase})",
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

    /// <summary>Deterministic conflict tie-break: the earlier-created CR (then the
    /// lexicographically smaller UID) wins; the other goes Failed.</summary>
    private static bool IsOlder(SqlAvailabilityGroupDatabase a, SqlAvailabilityGroupDatabase b)
    {
        var ta = a.Metadata.CreationTimestamp ?? DateTime.MaxValue;
        var tb = b.Metadata.CreationTimestamp ?? DateTime.MaxValue;
        if (ta != tb) return ta < tb;
        return string.CompareOrdinal(a.Metadata.Uid, b.Metadata.Uid) < 0;
    }

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
            return await UpdatePhase(entity, Phases.WaitingForAg, "AgNotFound",
                $"SqlAvailabilityGroup '{agName}' not found in namespace '{ns}'", ct);
        }

        // Degraded with a primary is an AG that works — often degraded precisely by what
        // this controller repairs (a former primary's database SUSPEND_FROM_PARTNER after every
        // failover). Waiting for Running there deadlocked: the redundancy never came back.
        var agUsable = ag.Status is { } agStatus
                       && (agStatus.Phase == Phases.Running
                           || (agStatus.Phase == Phases.Degraded && !string.IsNullOrEmpty(agStatus.PrimaryReplica)));
        if (!agUsable)
        {
            logger.LogInformation("AG {Ag} is in phase {Phase}, waiting for Running", agName, ag.Status?.Phase);
            await eventPublisher.PublishAsync(entity, "DatabaseWaitingForAg",
                $"Waiting for AG {agName} to reach Running phase (current: {ag.Status?.Phase})", ct: ct);
            return await UpdatePhase(entity, Phases.WaitingForAg, "AgNotReady",
                $"AG '{agName}' is in phase '{ag.Status?.Phase}', waiting for Running", ct);
        }

        // Re-validate uniqueness at the reconcile (source of authority). Two SAGDB
        // CRs created concurrently both pass the webhook TOCTOU window; here the loser — the
        // one with the later creation timestamp — goes Failed with an explicit condition.
        var siblings = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct);
        var conflict = siblings.FirstOrDefault(s =>
            s.Metadata.Uid != entity.Metadata.Uid
            && s.Spec.AvailabilityGroupRef.Name == agName
            && s.Spec.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase)
            && IsOlder(s, entity));
        if (conflict != null)
            return await UpdatePhase(entity, Phases.Failed, "DuplicateDatabase",
                $"Database '{dbName}' in AG '{agName}' is already owned by SqlAvailabilityGroupDatabase '{conflict.Metadata.Name}'", ct);

        // 2. Connect to primary and validate the database
        var primaryReplica = ag.Status.PrimaryReplica;
        if (primaryReplica == null)
            return await UpdatePhase(entity, Phases.WaitingForAg, "NoPrimary", "AG has no primary replica", ct);

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
            return await UpdatePhase(entity, Phases.Failed, "SaPasswordNotFound", "Cannot resolve SA password from AG", ct);

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
                return await UpdatePhase(entity, Phases.Failed, "DatabaseNotFound",
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
                        await UpdatePhase(entity, Phases.Synchronized, "AlreadyInAg",
                            $"Database already in AG {agName}", ct), ct);
                }

                await eventPublisher.PublishAsync(entity, "DatabaseBelongsToOtherAg",
                    $"Database '{dbName}' belongs to AG '{existingAg}'", "Warning", ct);
                return await UpdatePhase(entity, Phases.Failed, "DatabaseBelongsToOtherAg",
                    $"Database '{dbName}' already belongs to AG '{existingAg}'", ct);
            }

            // 5. Prepare and add database to AG
            entity = await UpdatePhase(entity, Phases.Adding, "AddingDatabase",
                $"Adding database '{dbName}' to AG '{agName}'", ct);

            var backupPath = $"/var/opt/mssql/backup/{dbName}_ag_init.bak";

            // Adding a database to an AG forces FULL recovery. If it was in SIMPLE, its
            // log now grows without bound until someone takes log backups — error 9002 (transaction log
            // full) is only a matter of time, and nothing said a word about it.
            var recoveryModel = await agManager.GetRecoveryModelAsync(conn, dbName, ct);
            if (!string.Equals(recoveryModel, "FULL", StringComparison.OrdinalIgnoreCase))
            {
                await eventPublisher.PublishAsync(entity, "RecoveryModelConverted",
                    $"Database '{dbName}' was in {recoveryModel} recovery and is being converted to FULL (an availability group " +
                    "requires it). Its transaction log will now grow until log backups are taken — schedule them (SqlAgentJob) " +
                    "or the log will fill the volume (error 9002).", "Warning", ct);
                ConditionOps.Set(entity.Status!.Conditions ??= [], "LogBackupsRequired", "True", "ConvertedToFullRecovery",
                    $"Database was converted from {recoveryModel} to FULL recovery: log backups are now required.");
            }

            // A full backup of a large database into a 10Gi PVC fails after hours, and the
            // reconcile retried it from scratch every 60 seconds — hammering the production primary.
            // Fail fast on an obvious shortage instead.
            if (await agManager.GetBackupSpaceEstimateAsync(conn, dbName, backupPath, ct) is { } space
                && space.FreeSpaceMb < space.DatabaseSizeMb)
            {
                var message =
                    $"Not enough free space to take the initialization backup of '{dbName}': the database is about " +
                    $"{space.DatabaseSizeMb} MB and only {space.FreeSpaceMb} MB are free on the volume. Enlarge the backup " +
                    "volume (spec.instanceTemplate.storage.backup) before adding this database.";
                await eventPublisher.PublishAsync(entity, "InsufficientBackupSpace", message, "Warning", ct);
                return await UpdatePhase(entity, Phases.Failed, "InsufficientBackupSpace", message, ct);
            }

            var backupTaken = await agManager.PrepareDatabaseForAgAsync(conn, dbName, backupPath, ct);
            if (backupTaken)
            {
                // Without a log chain the log of a FULL database is truncated at each
                // checkpoint. Once the chain is started the log grows until a log backup — the AG
                // filled its 4 Gi volume (9002) with no word, because the database was FULL already
                // and only a recovery model conversion raised LogBackupsRequired.
                await eventPublisher.PublishAsync(entity, "InitializationBackupTaken",
                    $"'{dbName}' had no log chain: took a full backup (a differential one when a differential base exists, " +
                    $"which it keeps) and a log backup to {backupPath} and {Path.ChangeExtension(backupPath, ".trn")}. " +
                    "They open the log chain: keep them with your backups. From now on the transaction log grows until " +
                    "log backups are taken — schedule them (SqlAgentJob) or the log will fill the volume (error 9002).", "Warning", ct);
                ConditionOps.Set(entity.Status!.Conditions ??= [], "LogBackupsRequired", "True", "LogChainStarted",
                    "The operator started the log chain of the database: log backups are now required.");
            }

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
            // The condition stays stable, the engine's error goes to the event — the failure
            // used to be visible in the condition only, and nowhere in `kubectl get events`.
            logger.LogError(ex, "Failed to add database {Db} to AG {Ag}", dbName, agName);
            await eventPublisher.PublishAsync(entity, "AddDatabaseFailed",
                $"Could not add database '{dbName}' to AG '{agName}': {ex.Message}", "Warning", ct);
            return await UpdatePhase(entity, Phases.Failed, "AddDatabaseFailed",
                $"Failed to add database '{dbName}' to AG '{agName}' (see events)", ct);
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
            return await UpdatePhase(entity, Phases.WaitingForAg, "NoPrimary", "AG primary not available", ct);

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
            return await UpdatePhase(entity, Phases.Failed, "SaPasswordNotFound",
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
                return await UpdatePhase(entity, Phases.Failed, "DatabaseNotInAg",
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

            // Determine phase from sync states. Suspension is not a synchronization_state_desc value
            // (a suspended database reports NOT SYNCHRONIZING): it is read from is_suspended.
            var allSynchronized = thisDbStates.All(d =>
                d.SynchronizationState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase));
            var anySuspended = thisDbStates.Any(IsSuspended);
            var anyNotSynchronizing = thisDbStates.Any(d =>
                d.SynchronizationState.Equals("NOT SYNCHRONIZING", StringComparison.OrdinalIgnoreCase) ||
                d.SynchronizationState.Equals("NOT_SYNCHRONIZING", StringComparison.OrdinalIgnoreCase));

            // Declarative suspension. SUSPEND runs on the primary (it stops the log flow to every
            // secondary); the matching RESUME must run on the primary too — resuming the secondaries
            // alone leaves the primary suspended, so flipping suspended back to false did nothing.
            var conditions = entity.Status.Conditions ??= [];
            var primaryRow = thisDbStates.FirstOrDefault(d => d.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase));
            var specSuspended = entity.Spec.DataMovement?.Suspended == true;
            if (specSuspended && primaryRow is not { IsSuspended: true })
            {
                await agManager.SuspendDataMovementAsync(conn, dbName, ct);
                ConditionOps.Set(conditions, SuspendedBySpecCondition, "True", "SuspendedBySpec",
                    "Data movement was suspended on the primary because spec.dataMovement.suspended is true");
                return await UpdatePhase(entity, Phases.Suspended, "DataMovementSuspended",
                    "Data movement suspended by spec", ct);
            }

            if (!specSuspended && WasSuspendedBySpec(entity.Status))
            {
                if (primaryRow is { IsSuspended: true })
                {
                    await agManager.ResumeDataMovementAsync(conn, dbName, ct);
                    await eventPublisher.PublishAsync(entity, "DataMovementResumed",
                        $"Issued HADR RESUME for '{dbName}' on the primary: spec.dataMovement.suspended is no longer true", "Normal", ct);
                }

                ConditionOps.Set(conditions, SuspendedBySpecCondition, "False", "NotSuspendedBySpec",
                    "spec.dataMovement.suspended is not set");
                return await UpdatePhase(entity, Phases.Synchronizing, "DataMovementResumed",
                    $"Resuming '{dbName}' on the primary", ct);
            }

            // Automatic seeding can fail (no space, permissions, a leftover RESTORING copy
            // on the secondary) and nothing observed it — the CR polled a database that would never
            // converge and re-issued RESUME forever. sys.dm_hadr_automatic_seeding is the engine's own
            // answer; surface it.
            if (entity.Status.Phase is Phases.Seeding or Phases.Adding
                && await agManager.GetSeedingStateAsync(conn, agName, dbName, ct) is { } seeding
                && seeding.CurrentState.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var seedingMessage =
                    $"Automatic seeding of '{dbName}' FAILED: {(string.IsNullOrEmpty(seeding.FailureState) ? "no reason reported" : seeding.FailureState)}. " +
                    "A common cause is a leftover copy of the database in RESTORING state on the secondary (from a previous " +
                    "attempt): it must be dropped on that replica before seeding can start again.";
                await eventPublisher.PublishAsync(entity, "SeedingFailed", seedingMessage, "Warning", ct);
                ConditionOps.Set(entity.Status.Conditions ??= [], "SeedingFailed", "True", "AutomaticSeedingFailed", seedingMessage);
                return await UpdatePhase(entity, Phases.Failed, "SeedingFailed", seedingMessage, ct);
            }

            // Resume any SECONDARY whose database is stalled — NOT SYNCHRONIZING / suspended, the
            // SUSPEND_FROM_PARTNER state every former primary lands in after a failover, or a
            // transient disconnect. HADR RESUME must run ON the stalled secondary: issuing it on the
            // primary connection is a no-op for the partner's local suspension, which is why a former
            // primary was stranded (AG left without redundancy) after every failover.
            //
            // But a RESUME is NOT harmless after a data-loss failover. The old primary's
            // copy is suspended precisely because its log diverged — it holds transactions that were
            // committed to applications and never replicated. RESUME makes the engine roll them back to
            // the new primary's LSN: the divergent work becomes irrecoverable (no tail-log backup is
            // possible afterwards). The operator must not make that call silently.
            //
            // requireManualResumeAfterDataLoss gates ONLY the replica that diverged in a
            // data-loss failover (the blocked list below). It used to switch every RESUME off as soon
            // as a dataMovement block existed (its default is true), so after any planned failover or
            // rolling update the former primary stayed suspended and the AG lost its redundancy.
            if (!specSuspended)
            {
                var stalledSecondaries = thisDbStates.Where(d =>
                    !d.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                    && (IsSuspended(d)
                        || d.SynchronizationState.Equals("NOT SYNCHRONIZING", StringComparison.OrdinalIgnoreCase)
                        || d.SynchronizationState.Equals("NOT_SYNCHRONIZING", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                // The replica the AG failed AWAY from, when that failover accepted data loss, is the one
                // holding divergent commits. Rejoining it discards them — require an explicit approval.
                var requireManualAfterDataLoss = entity.Spec.DataMovement?.RequireManualResumeAfterDataLoss ?? true;
                var divergent = ag.Status?.LastFailover is { DataLoss: true, From: { } from } ? from : null;
                var approved = entity.Metadata.Annotations?.TryGetValue(ApproveDataLossResumeAnnotation, out var v) == true
                               && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

                var blocked = divergent is null || approved || !requireManualAfterDataLoss
                    ? []
                    : stalledSecondaries.Where(s => s.ReplicaName.Equals(divergent, StringComparison.OrdinalIgnoreCase)).ToList();

                if (blocked.Count > 0)
                {
                    var rejoinMessage =
                        $"Replica {divergent} is stalled after a forced failover that accepted data loss " +
                        $"({ag!.Status!.LastFailover!.UnsentLogKb?.ToString() ?? "unknown"} KB of unsent log at the time). " +
                        "Resuming it would roll back the transactions it holds and never replicated — they become unrecoverable. " +
                        $"Take a tail-log backup there if you need them, then approve with the annotation " +
                        $"{ApproveDataLossResumeAnnotation}=true (or set dataMovement.requireManualResumeAfterDataLoss=false).";
                    await eventPublisher.PublishAsync(entity, "ManualRejoinRequired", rejoinMessage, "Warning", ct);
                    ConditionOps.Set(entity.Status.Conditions ??= [], "ManualRejoinRequired", "True", "DataLossFailover", rejoinMessage);
                    stalledSecondaries = stalledSecondaries.Except(blocked).ToList();
                }
                else if (approved)
                {
                    ConditionOps.Set(entity.Status.Conditions ??= [], "ManualRejoinRequired", "False", "Approved",
                        "The rejoin of the divergent replica was explicitly approved");
                }

                // A database that re-suspends as fast as it is resumed used to be reported
                // as "Synchronizing" forever, hiding the real state. Count the attempts and stop lying.
                var attempts = entity.Status.ConsecutiveResumeAttempts;
                var now = _clock.GetUtcNow().UtcDateTime;
                var ineffective = entity.Status.Conditions?.FirstOrDefault(c => c.Type == ResumeIneffectiveCondition);
                var rearmed = false;
                // The attempts were burned while the replica could not resume at all
                // (isolated, then RESOLVING); once that was fixed nothing resumed it ever again — the
                // counter only resets when nothing is suspended, which takes a RESUME. A new round of
                // attempts starts once the condition has been True for a while.
                if (stalledSecondaries.Count > 0 && attempts >= MaxConsecutiveResumeAttempts
                    && ineffective is { Status: "True" } && now - ineffective.LastTransitionTime >= ResumeRearmInterval)
                {
                    ConditionOps.Set(entity.Status.Conditions!, ResumeIneffectiveCondition, "False", "Rearmed",
                        $"HADR RESUME of '{dbName}' is retried: the last round of attempts ended more than " +
                        $"{(int)ResumeRearmInterval.TotalMinutes} minutes ago.", now: now);
                    attempts = 0;
                    rearmed = true;
                }

                if (stalledSecondaries.Count > 0 && attempts >= MaxConsecutiveResumeAttempts)
                {
                    var resumeMessage =
                        $"HADR RESUME has been issued {attempts} times for '{dbName}' without the database reaching " +
                        "SYNCHRONIZING/SYNCHRONIZED. Something on the secondary prevents it (log chain broken, disk full, " +
                        $"diverged copy); a re-seed is likely required. The operator retries in {(int)ResumeRearmInterval.TotalMinutes} minutes.";
                    await eventPublisher.PublishAsync(entity, "ResumeIneffective", resumeMessage, "Warning", ct);
                    ConditionOps.Set(entity.Status.Conditions ??= [], ResumeIneffectiveCondition, "True", "ResumeIneffective", resumeMessage, now: now);
                    stalledSecondaries = [];
                }

                var resumed = new List<string>();
                foreach (var sec in stalledSecondaries)
                {
                    try
                    {
                        var secondaryFqdn = $"{sec.ReplicaName}.{headlessService}.{ns}.svc.cluster.local";
                        await using var secondaryConn = await connectionFactory.CreateProbeConnectionAsync(
                            secondaryFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                        // After a clusterless failover, a secondary that was neither the
                        // old primary nor the target stays SUSPEND_FROM_PARTNER on the primary while
                        // it does not see itself suspended — a RESUME there is a no-op, five times
                        // over. Once a plain RESUME did not converge, the secondary is suspended and
                        // resumed: that renegotiates data movement with the new primary.
                        if (attempts > 0 || rearmed)
                            await agManager.SuspendDataMovementAsync(secondaryConn, dbName, ct);
                        await agManager.ResumeDataMovementAsync(secondaryConn, dbName, ct);
                        resumed.Add(sec.ReplicaName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "HADR RESUME for {Db} on secondary {Replica} failed; manual re-seed may be required", dbName, sec.ReplicaName);
                    }
                }

                if (resumed.Count > 0)
                {
                    entity.Status.ConsecutiveResumeAttempts = attempts + 1;
                    await eventPublisher.PublishAsync(entity, "DataMovementResumed",
                        $"Issued HADR RESUME for '{dbName}' on secondary replica(s): {string.Join(", ", resumed)}", "Normal", ct);
                    return await UpdatePhase(entity, Phases.Synchronizing, "DataMovementResumed",
                        $"Resuming '{dbName}' on {string.Join(", ", resumed)}", ct);
                }
            }

            // Nothing is stalled: the resume loop converged.
            if (!anySuspended)
            {
                entity.Status.ConsecutiveResumeAttempts = 0;
                // A condition left True dates the next round from a long-gone episode, and one
                // left Rearmed says a retry is under way on a database that is synchronized.
                if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == ResumeIneffectiveCondition) is { Reason: not "Converged" })
                    ConditionOps.Set(entity.Status.Conditions, ResumeIneffectiveCondition, "False", "Converged",
                        $"Data movement of '{dbName}' is no longer suspended.", now: _clock.GetUtcNow().UtcDateTime);
            }

            string newPhase;
            string reason;
            string message;

            if (anySuspended)
            {
                newPhase = Phases.Suspended;
                reason = "DataMovementSuspended";
                message = $"Database '{dbName}' has suspended data movement on one or more replicas";
            }
            else if (anyNotSynchronizing)
            {
                newPhase = Phases.NotSynchronizing;
                reason = "NotSynchronizing";
                message = $"Database '{dbName}' is not synchronizing on one or more replicas";
            }
            else if (allSynchronized)
            {
                newPhase = Phases.Synchronized;
                reason = "DatabaseSynchronized";
                message = $"Database '{dbName}' synchronized across all replicas";
            }
            else
            {
                newPhase = Phases.Synchronizing;
                reason = "SynchronizationInProgress";
                message = $"Database '{dbName}' synchronization in progress";
            }

            // No per-pass timestamp in the status. A "last observed at" rewritten on every pass
            // changed the status hash each time, so every pass wrote the status, the MODIFIED event
            // re-enqueued the key at once, and each SAGDB reconciled in a tight loop. The blind window
            // is tracked by the SyncStateUnknown condition, whose transition time only moves on change.
            ConditionOps.Set(conditions, "SyncStateUnknown", "False", "Observed",
                "The synchronization state was read from the primary in the last pass");
            return await UpdatePhase(entity, newPhase, reason, message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A persistent failure to reach the primary used to leave a stale
            // "Synchronized" phase in place forever — the status looked healthy while the operator had
            // in fact been blind for hours. Say so after a grace period.
            logger.LogWarning(ex, "Failed to check sync state for {Db} in AG {Ag}", dbName, agName);
            return await MarkSyncStateUnreadableAsync(entity, dbName, ex, ct);
        }
    }

    /// <summary>Grace period before a blind operator says so in the status.</summary>
    private static readonly TimeSpan SyncStateUnknownGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tracks a failure to read the synchronization state with stable status writes only:
    /// the first failure moves SyncStateUnknown to Unknown (its transition time marks the
    /// start of the blind window), and after the grace period to True. Messages carry no exception
    /// text or measured duration — those change on every pass and would rewrite the status each
    /// time; the details go to the event and the logs.
    /// </summary>
    private async Task<SqlAvailabilityGroupDatabase> MarkSyncStateUnreadableAsync(
        SqlAvailabilityGroupDatabase entity, string dbName, Exception ex, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupDatabaseStatus();
        var conditions = entity.Status.Conditions ??= [];
        var current = conditions.FirstOrDefault(c => c.Type == "SyncStateUnknown");
        // A statement error (severity 16 and below: an invalid column, a permission) is
        // not an unreachable primary — the status said so while the primary answered every pass.
        var reason = ex is Microsoft.Data.SqlClient.SqlException { Class: <= 16 } ? "SyncStateQueryFailed" : "PrimaryUnreachable";

        if (current?.Status is not ("Unknown" or "True"))
        {
            ConditionOps.Set(conditions, "SyncStateUnknown", "Unknown", reason,
                $"The synchronization state of '{dbName}' could not be read from the primary; retrying.",
                now: _clock.GetUtcNow().UtcDateTime);
            return await kubernetesClient.UpdateStatusAsync(entity, ct);
        }

        if (current.Status == "Unknown" && _clock.GetUtcNow().UtcDateTime - current.LastTransitionTime > SyncStateUnknownGrace)
        {
            var message =
                $"The synchronization state of '{dbName}' could not be read for more than " +
                $"{(int)SyncStateUnknownGrace.TotalMinutes} minutes. The phase below is the last known one and may be out of date.";
            ConditionOps.Set(conditions, "SyncStateUnknown", "True", reason, message);
            await eventPublisher.PublishAsync(entity, "SyncStateUnknown", $"{message} Last error: {ex.Message}", "Warning", ct);
            return await kubernetesClient.UpdateStatusAsync(entity, ct);
        }

        return entity; // Will be requeued
    }

    private static bool IsSuspended(AgDatabaseStateInfo d) =>
        d.IsSuspended || d.SynchronizationState.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Condition recording that the operator itself suspended the database, so it
    /// only resumes the primary when it was the one that suspended it.</summary>
    private const string SuspendedBySpecCondition = "SuspendedBySpec";

    private static bool WasSuspendedBySpec(SqlAvailabilityGroupDatabaseStatus status)
    {
        var condition = status.Conditions?.FirstOrDefault(c => c.Type == SuspendedBySpecCondition);
        // Before this condition existed, the Suspended phase could only be reached through the spec.
        return condition is null ? status.Phase == Phases.Suspended : condition.Status == "True";
    }

    /// <summary>Explicit approval to rejoin a replica that diverged during a data-loss failover
    /// its unreplicated commits are rolled back when it resumes.</summary>
    public const string ApproveDataLossResumeAnnotation = "mssql-operator.evariops.com/approve-data-loss-resume";

    /// <summary>After this many fruitless RESUME attempts the operator stops and reports the truth
    /// instead of re-suspending the phase to "Synchronizing" forever.</summary>
    private const int MaxConsecutiveResumeAttempts = 5;

    /// <summary>How long the operator waits after a fruitless round before it tries again.</summary>
    private static readonly TimeSpan ResumeRearmInterval = TimeSpan.FromMinutes(5);

    private const string ResumeIneffectiveCondition = "ResumeIneffective";

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
