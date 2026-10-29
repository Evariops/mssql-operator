using MsSqlOperator.Services.Actuation;
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
    TimeProvider? timeProvider = null,
    Services.Metrics.OperatorMetrics? metrics = null,
    IVolumeAutoGrower? volumeAutoGrower = null,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null,
    Services.Health.IAgStateStore? stateStore = null,
    Services.ConnectionFactory.TargetLimiter? targetLimiter = null,
    LongOperations? longOperations = null) : IEntityController<SqlAvailabilityGroupDatabase>
{
    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

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
        if (_watchLog && requeue > LogWatchInterval)
            requeue = LogWatchInterval;
        return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity, requeue);
    }

    public Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> DeletedAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAvailabilityGroupDatabase {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity));
    }

    // ── Phase: Pending / WaitingForAg ─────────────────────

    private const string WaitingForBackupWindow = "WaitingForBackupWindow";
    private const string WaitingForBackupSlot = "WaitingForBackupSlot";
    private const string InitializationBackupRunning = "InitializationBackupRunning";

    /// <summary>One initialization backup at a time per AG; <see cref="Services.ConnectionFactory.TargetLimiter.None"/>
    /// without a limiter.</summary>
    private IAsyncDisposable? TryBackupSlot(string ns, string agName)
        => targetLimiter is null
            ? Services.ConnectionFactory.TargetLimiter.None
            : targetLimiter.TryAcquire(Services.ConnectionFactory.TargetLimiter.BackupKeyOf(TargetKinds.AvailabilityGroup, ns, agName), slots: 1);

    /// <summary>The checks before an initialization backup: the recovery model it converts (said), the
    /// room it needs (refused when obviously short). The refusal, or null.</summary>
    private async Task<SqlAvailabilityGroupDatabase?> CheckBeforeBackupAsync(SqlAvailabilityGroupDatabase entity,
        Microsoft.Data.SqlClient.SqlConnection conn, string dbName, string backupPath, CancellationToken ct)
    {
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
        return null;
    }

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
            // The initial backup and the join — the long commands set their own timeout.
            await using var conn = await connectionFactory.OpenAsync(primaryFqdn, SqlEndpoints.ContainerPort, saPassword, SqlProfile.LongRunning, "master", ct);

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

            // The initialization backup reads the whole database on the primary — inside the
            // window when one is set, and one at a time per AG. The waits stay Pending (no Adding
            // written then undone on every pass).
            var takesBackup = !await agManager.HasLogChainAsync(conn, dbName, ct);
            var backupPath = $"/var/opt/mssql/backup/{dbName}_ag_init.bak";
            var backupKey = $"agd-init-backup/{ns}/{agName}/{dbName}";
            var backupState = longOperations?.StateOf(backupKey);
            // A backup starts the log chain of the copy it was taken on. Done, with no chain
            // now, it was taken on another copy — the primary changed since (a failover during the
            // creation), or the database was dropped and created again. Taken for this copy's, it
            // skipped its backup and the join failed (error 1475): it is taken again.
            if (takesBackup && backupState is { Running: false, Error: null })
            {
                longOperations!.Forget(backupKey);
                backupState = null;
            }
            if (takesBackup && backupState is null)
            {
                if (entity.Spec.Initialization?.BackupWindow is { } window && !MaintenanceWindows.IsOpen(window, _clock.GetUtcNow()))
                    return await UpdatePhase(entity, Phases.Pending, WaitingForBackupWindow,
                        $"Database '{dbName}' has no log chain: its initialization backup waits for the window opening at {window.Start} UTC " +
                        $"({window.DurationMinutes} min)", ct);
                // One started by an earlier process (or leader) may still run on the server.
                if (longOperations is not null && await agManager.GetBackupProgressAsync(conn, dbName, ct) is { } elsewhere)
                    return await UpdatePhase(entity, Phases.Pending, InitializationBackupRunning,
                        $"A backup of '{dbName}' is running on the primary ({elsewhere:0}%): the initialization waits for it", ct);
                if (await CheckBeforeBackupAsync(entity, conn, dbName, backupPath, ct) is { } refused)
                    return refused;
            }

            var backupTaken = false;
            // The chain exists while the operator's backup still runs (between its full and its log
            // backup) and once it is done: the backup is followed up all the same. Left aside, its
            // warning was lost, and its result stayed for a later database of that name.
            if (longOperations is not null && (takesBackup || backupState is not null))
            {
                // Started here, run in the background, followed by the next passes — a backup
                // of hours no longer holds a worker, nor hits the reconcile timeout.
                if (backupState is null)
                {
                    var slot = TryBackupSlot(ns, agName);
                    if (slot is null)
                        return await UpdatePhase(entity, Phases.Pending, WaitingForBackupSlot,
                            $"Another initialization backup runs on AG '{agName}': '{dbName}' waits for it", ct);
                    // The slot is held by the operation, for as long as the backup runs.
                    if (!longOperations.TryStart(backupKey, async token =>
                        {
                            await using (slot)
                            {
                                await using var backupConn = await connectionFactory.OpenAsync(primaryFqdn, SqlEndpoints.ContainerPort, saPassword,
                                    SqlProfile.LongRunning, "master", token);
                                return await agManager.PrepareDatabaseForAgAsync(backupConn, dbName, backupPath, token);
                            }
                        }))
                        await slot.DisposeAsync();
                    // The backup of a small database takes a second: followed up here, the database
                    // joins in this pass instead of the next one, 30 s later.
                    backupState = await longOperations.WaitAsync(backupKey, ct);
                    if (backupState is not { Running: false })
                        return await UpdatePhase(entity, Phases.Pending, InitializationBackupRunning,
                            $"Initialization backup of '{dbName}' started on {primaryReplica}", ct);
                }
                if (backupState.Running)
                {
                    var progress = await agManager.GetBackupProgressAsync(conn, dbName, ct);
                    return await UpdatePhase(entity, Phases.Pending, InitializationBackupRunning,
                        $"Initialization backup of '{dbName}' running since {backupState.StartedAt:O}" +
                        (progress is { } p ? $" ({p:0}%)" : ""), ct);
                }
                longOperations.Forget(backupKey);
                if (backupState.Error is { } error)
                {
                    await eventPublisher.PublishAsync(entity, "InitializationBackupFailed",
                        $"The initialization backup of '{dbName}' failed: {error.Message}", "Warning", ct);
                    return await UpdatePhase(entity, Phases.Failed, "InitializationBackupFailed",
                        $"The initialization backup of '{dbName}' failed (see events); " +
                        (takesBackup ? "it is taken again on the next pass" : "its log chain exists: the next pass adds the database"), ct);
                }
                backupTaken = backupState.Result is true;
            }
            else if (takesBackup)
            {
                // No background pool (tests, older wiring): the backup runs inside the pass.
                await using var slot = TryBackupSlot(ns, agName);
                if (targetLimiter is not null && slot is null)
                    return await UpdatePhase(entity, Phases.Pending, WaitingForBackupSlot,
                        $"Another initialization backup runs on AG '{agName}': '{dbName}' waits for it", ct);
                backupTaken = await agManager.PrepareDatabaseForAgAsync(conn, dbName, backupPath, ct);
            }
            else
            {
                // A log chain already: FULL recovery is checked, no backup taken.
                backupTaken = await agManager.PrepareDatabaseForAgAsync(conn, dbName, backupPath, ct);
            }

            // 5. Add database to AG
            entity = await UpdatePhase(entity, Phases.Adding, "AddingDatabase",
                $"Adding database '{dbName}' to AG '{agName}'", ct);

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
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        // The primary of the last state read, when fresh, over the status.
        if (stateStore?.FreshPrimary(ns, agName) is { } observed && observed != ag.Status.PrimaryReplica)
            ag.Status.PrimaryReplica = observed;

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
            return await UpdatePhase(entity, Phases.Failed, "SaPasswordNotFound",
                "Cannot resolve SA password from AG spec", ct);

        var headlessService = $"{agName}-headless";
        var primaryFqdn = $"{ag.Status.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            // RESUME, SUSPEND, re-seed — HADR commands that must not wait on a lock for ever.
            await using var conn = await connectionFactory.OpenAsync(primaryFqdn, SqlEndpoints.ContainerPort, saPassword, SqlProfile.Hadr, "master", ct);

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

            await ReportLogSpaceAsync(entity, ag, conn, dbName, thisDbStates, ct);

            // No RESUME and no re-seed while the topology changes — they would act on a
            // primary about to stop being one. The phase stays as it was: this is a pause.
            var topologyConditions = entity.Status.Conditions ??= [];
            if (AgTopology.ChangeInProgress(ag) is { } change)
            {
                ConditionOps.Set(topologyConditions, AgTopology.Condition, "True", "TopologyChanging",
                    $"{change}: RESUME and re-seed of '{dbName}' wait for it to end.", now: _clock.GetUtcNow().UtcDateTime);
                return await kubernetesClient.UpdateStatusAsync(entity, ct);
            }
            if (topologyConditions.FirstOrDefault(c => c.Type == AgTopology.Condition) is { Status: "True" })
                ConditionOps.Set(topologyConditions, AgTopology.Condition, "False", "TopologyStable",
                    $"The topology of '{agName}' is stable.", now: _clock.GetUtcNow().UtcDateTime);

            if (await ConsumeReseedRequestAsync(entity, ag, conn, saPassword, headlessService, ns, agName, dbName, thisDbStates, ct) is { } reseeded)
                return reseeded;
            if (await RetryFailedSeedingAsync(entity, conn, saPassword, headlessService, ns, agName, dbName, thisDbStates, ct) is { } retried)
                return retried;

            // Determine phase from sync states. Suspension is not a synchronization_state_desc value
            // (a suspended database reports NOT SYNCHRONIZING): it is read from is_suspended.
            var allSynchronized = thisDbStates.All(d =>
                d.SynchronizationState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase));
            var anySuspended = thisDbStates.Any(IsSuspended);
            var anyNotSynchronizing = thisDbStates.Any(IsNotSynchronizing);

            // Automatic seeding fills a secondary while the primary reports it NOT
            // SYNCHRONIZING for the whole transfer. Taken for a stalled one, it was RESUMEd on every
            // pass (error 35242: the copy is not joined yet — counted all the same), ResumeIneffective
            // came after a minute, and two rounds later a re-seed that dropped the copy being seeded: a
            // seeding longer than the re-seed cooldown never ended. It is left to the seeding.
            //
            // The engine moves it through SEEDING, then CATCHUP (the log written meanwhile, long under
            // load); a re-seed's own seeding shows up a fraction of a second after it, the previous one
            // being the latest until then. A seeding the engine stopped following stays SEEDING or
            // CATCHUP for good: past the time its size can take, it is treated as stalled.
            //
            // The seeding history also keeps a failed attempt for a replica that synchronizes
            // since (one restarted by a failover): read for the whole database, it failed the re-seed of
            // another replica. Only the last seeding of a replica that does not synchronize counts.
            var seedingReplicas = new List<string>();
            (string Replica, ReplicaSeedingInfo Seeding)? failedSeeding = null;
            TimeSpan? seedingBound = null;
            var seenAt = _clock.GetUtcNow().UtcDateTime;
            foreach (var d in thisDbStates.Where(d => IsNotSynchronizing(d) && !IsSuspended(d)
                                                      && !d.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)))
            {
                var latest = await agManager.GetReplicaSeedingAsync(conn, agName, dbName, d.ReplicaName, ct);
                var reseedStarting = entity.Status.ReseededAt?.GetValueOrDefault(d.ReplicaName) is { } reseededAt
                                     && seenAt - reseededAt < ReseedStartGrace
                                     && (latest is null || latest.StartTime < reseededAt);
                if (reseedStarting
                    || (latest is { CurrentState: "SEEDING" or "CATCHUP" }
                        && seenAt - latest.StartTime < (seedingBound ??= await SeedingBoundAsync(conn, dbName, ct))))
                    seedingReplicas.Add(d.ReplicaName);
                else if (latest is { CurrentState: "FAILED" })
                    failedSeeding ??= (d.ReplicaName, latest);
            }

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
            if (entity.Status.Phase is Phases.Seeding or Phases.Adding && failedSeeding is (var failedReplica, var seeding))
            {
                var seedingMessage =
                    $"Automatic seeding of '{dbName}' to {failedReplica} FAILED: {(string.IsNullOrEmpty(seeding.FailureState) ? "no reason reported" : seeding.FailureState)}. " +
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
                    && (IsSuspended(d) || IsNotSynchronizing(d))
                    && !seedingReplicas.Contains(d.ReplicaName))
                    .ToList();

                // The replica the AG failed AWAY from, when that failover accepted data loss, is the one
                // holding divergent commits. Rejoining it discards them — require an explicit approval.
                // Every open divergence of the AG counts, not only the last failover's: a planned
                // failover after a forced one no longer lets the divergent replica rejoin unasked.
                var requireManualAfterDataLoss = entity.Spec.DataMovement?.RequireManualResumeAfterDataLoss ?? true;
                // The divergences of the AG's status and those the database itself carries.
                var divergences = await DivergencesAsync(ag, conn, dbName, entity.Status.ResolvedDivergences ?? [], ct);
                var approvedNow = await ConsumeDataLossApprovalAsync(entity, divergences, ct);
                if (approvedNow.Count > 0)
                    await ForgetMarkersAsync(conn, dbName, approvedNow, ct);
                var approvedReplicas = divergences.Where(d => approvedNow.Contains(d.Id)).Select(d => d.From).OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var resolved = entity.Status.ResolvedDivergences ?? [];
                var pending = requireManualAfterDataLoss ? divergences.Where(d => !resolved.Contains(d.Id)).ToList() : [];
                var approved = pending.Count == 0;

                var blocked = stalledSecondaries
                    .Where(s => pending.Any(d => s.ReplicaName.Equals(d.From, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (blocked.Count > 0)
                {
                    var ids = pending.Where(d => blocked.Any(b => b.ReplicaName.Equals(d.From, StringComparison.OrdinalIgnoreCase)))
                        .Select(d => d.Id).ToList();
                    var rejoinMessage =
                        $"Replica(s) {string.Join(", ", blocked.Select(b => b.ReplicaName))} stalled after forced failover(s) that " +
                        $"accepted data loss ({string.Join(", ", ids)}). Resuming would roll back the transactions they hold and never " +
                        "replicated — they become unrecoverable. Take a tail-log backup there if you need them, then approve with the " +
                        $"annotation {ApproveDataLossResumeAnnotation}={string.Join(",", ids)} (or set " +
                        "dataMovement.requireManualResumeAfterDataLoss=false).";
                    await eventPublisher.PublishAsync(entity, "ManualRejoinRequired", rejoinMessage, "Warning", ct);
                    ConditionOps.Set(entity.Status.Conditions ??= [], "ManualRejoinRequired", "True", "DataLossFailover", rejoinMessage);
                    stalledSecondaries = stalledSecondaries.Except(blocked).ToList();
                }
                else if (approvedNow.Count > 0)
                {
                    ConditionOps.Set(entity.Status.Conditions ??= [], "ManualRejoinRequired", "False", "Approved",
                        "The rejoin of the divergent replica was explicitly approved");
                }
                else if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == "ManualRejoinRequired") is { Status: "True" })
                {
                    // The divergent replica rejoined or was re-seeded — the condition used to
                    // stay True for good, holding NeedsAttention with it.
                    ConditionOps.Set(entity.Status.Conditions, "ManualRejoinRequired", "False", "NoDivergentReplica",
                        "No replica holding divergent log waits for an approval any more");
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
                    if (ineffective is not { Status: "True" })
                        entity.Status.FruitlessResumeRounds++;
                    if (entity.Status.FruitlessResumeRounds >= ReseedAfterFruitlessRounds
                        && await TryAutoReseedAsync(entity, ag!, conn, saPassword, headlessService, ns, agName, dbName,
                            stalledSecondaries.Select(s => s.ReplicaName).ToList(), BystanderFork(ag, pending, dbName), ct) is { } autoReseeded)
                        return autoReseeded;

                    var resumeMessage =
                        $"HADR RESUME has been issued {attempts} times for '{dbName}' without the database reaching " +
                        "SYNCHRONIZING/SYNCHRONIZED. Something on the secondary prevents it (log chain broken, disk full, " +
                        $"diverged copy); a re-seed is likely required. The operator retries in {(int)ResumeRearmInterval.TotalMinutes} minutes.";
                    await eventPublisher.PublishAsync(entity, "ResumeIneffective", resumeMessage, "Warning", ct);
                    ConditionOps.Set(entity.Status.Conditions ??= [], ResumeIneffectiveCondition, "True", "ResumeIneffective", resumeMessage, now: now);
                    stalledSecondaries = [];
                }

                var resumed = new List<string>();
                var tried = 0;
                var divergentBystanders = new List<string>();
                var forkLsn = BystanderFork(ag, pending, dbName);
                foreach (var sec in stalledSecondaries)
                {
                    try
                    {
                        var secondaryFqdn = $"{sec.ReplicaName}.{headlessService}.{ns}.svc.cluster.local";
                        await using var secondaryConn = await connectionFactory.CreateProbeConnectionAsync(
                            secondaryFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                        // The primary lists a former primary as SECONDARY while it is still
                        // RESOLVING — or resurrected as PRIMARY, before the orchestrator demotes it. A
                        // RESUME there does nothing, and the five attempts were spent before it took
                        // the secondary role — then five more minutes before the next round.
                        var localRole = await agManager.GetLocalReplicaRoleAsync(secondaryConn, agName, ct);
                        if (!string.Equals(localRole, "SECONDARY", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation("HADR RESUME for {Db} on {Replica} deferred: the replica reports {Role}, not secondary yet",
                                dbName, sec.ReplicaName, localRole ?? "no role");
                            continue;
                        }
                        // A secondary cut from the primary (a HADR partition) cannot resume
                        // either. The attempts were spent during the partition, and ResumeIneffective
                        // called for a re-seed as it ended — while the database resynchronized on its own.
                        if (await agManager.IsConnectedToPrimaryAsync(secondaryConn, agName, ct) == false)
                        {
                            logger.LogInformation("HADR RESUME for {Db} on {Replica} deferred: the replica is not connected to the primary",
                                dbName, sec.ReplicaName);
                            continue;
                        }
                        // After a data-loss failover, a bystander had followed the
                        // resurrected previous primary for seconds and synchronized its divergent log:
                        // it holds commits the new primary never had. Resuming it discards them, the
                        // same irreversible call as for the previous primary — not silently.
                        if (forkLsn is { } fork
                            && (await agManager.GetDatabaseStatesAsync(secondaryConn, agName, ct)).FirstOrDefault(d =>
                                d.ReplicaName.Equals(sec.ReplicaName, StringComparison.OrdinalIgnoreCase)
                                && d.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase)) is { LastHardenedLsn: { } lsn }
                            && lsn > fork)
                        {
                            divergentBystanders.Add(sec.ReplicaName);
                            continue;
                        }
                        tried++;
                        // After a clusterless failover, a secondary that was neither the
                        // old primary nor the target stays SUSPEND_FROM_PARTNER on the primary while
                        // it does not see itself suspended — a RESUME there is a no-op, five times
                        // over. Once a plain RESUME did not converge, the secondary is suspended and
                        // resumed: that renegotiates data movement with the new primary.
                        //
                        // A secondary that had followed a resurrected old primary stayed
                        // tied to it once demoted; the cycle only took after SET (ROLE = SECONDARY)
                        // pointed it back at the real primary — a no-op on a sound secondary.
                        if (attempts > 0 || rearmed)
                        {
                            try
                            {
                                await agManager.SetSecondaryRoleAsync(secondaryConn, agName, ct);
                            }
                            catch (Microsoft.Data.SqlClient.SqlException ex)
                            {
                                logger.LogDebug(ex, "SET (ROLE = SECONDARY) on {Replica} refused; cycling data movement anyway", sec.ReplicaName);
                            }
                            await agManager.SuspendDataMovementAsync(secondaryConn, dbName, ct);
                        }
                        if (approvedReplicas.Contains(sec.ReplicaName))
                            // Resuming a divergent replica discards its commits — a Destructive
                            // action, approved by the human whose annotation was just consumed.
                            await Gateway.RunAsync(entity, new MsSqlOperator.Services.Actuation.ActionRequest(
                                    MsSqlOperator.Services.Actuation.ActionType.ResumeDivergentReplica, sec.ReplicaName,
                                    MsSqlOperator.Services.Actuation.ActuationGateway.Epoch(entity),
                                    $"resume divergent replica {sec.ReplicaName} of {dbName}, discarding its unreplicated commits", dbName, sec.ReplicaName),
                                c => agManager.ResumeDataMovementAsync(secondaryConn, dbName, c), ct,
                                MsSqlOperator.Services.Actuation.ActuationIntent.HumanAnnotation);
                        else
                            await agManager.ResumeDataMovementAsync(secondaryConn, dbName, ct);
                        resumed.Add(sec.ReplicaName);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "HADR RESUME for {Db} on secondary {Replica} failed; manual re-seed may be required", dbName, sec.ReplicaName);
                    }
                }

                if (divergentBystanders.Count > 0)
                {
                    var bystanderMessage =
                        $"Replica(s) {string.Join(", ", divergentBystanders)} hold log of '{dbName}' beyond the point the AG was " +
                        $"force-failed over to {ag!.Status!.LastFailover!.To} (LSN {forkLsn}): they synchronized from the previous primary " +
                        "after it lost the AG. Resuming them rolls those transactions back. Take a tail-log backup there if you need them, " +
                        $"then approve with the annotation {ApproveDataLossResumeAnnotation}=" +
                        $"{AgDivergences.IdOf(ag.Status.LastFailover.Timestamp, ag.Status.LastFailover.To)}. If a replica stays NOT SYNCHRONIZING " +
                        "after the approval, re-seed it: SET HADR OFF and DROP DATABASE on it (with reconcile-paused on this resource), " +
                        "then MODIFY REPLICA … WITH (SEEDING_MODE = AUTOMATIC) on the primary.";
                    await eventPublisher.PublishAsync(entity, "ManualRejoinRequired", bystanderMessage, "Warning", ct);
                    ConditionOps.Set(entity.Status.Conditions ??= [], "ManualRejoinRequired", "True", "DataLossFailover", bystanderMessage);
                }

                // A RESUME that throws on every pass (the copy was taken out of the AG on the
                // replica) is an attempt too — counted only on success, it never reached
                // ResumeIneffective nor the re-seed, and the replica stayed out for good.
                if (tried > 0)
                    entity.Status.ConsecutiveResumeAttempts = attempts + 1;
                if (resumed.Count > 0)
                {
                    await eventPublisher.PublishAsync(entity, "DataMovementResumed",
                        $"Issued HADR RESUME for '{dbName}' on secondary replica(s): {string.Join(", ", resumed)}", "Normal", ct);
                    return await UpdatePhase(entity, Phases.Synchronizing, "DataMovementResumed",
                        $"Resuming '{dbName}' on {string.Join(", ", resumed)}", ct);
                }
            }

            // Nothing is stalled: the resume loop converged. A secondary NOT SYNCHRONIZING
            // without being suspended is stalled too — counted as converged, it reset the attempts on
            // every pass and never escalated.
            if (!anySuspended && !anyNotSynchronizing)
            {
                entity.Status.ConsecutiveResumeAttempts = 0;
                entity.Status.FruitlessResumeRounds = 0;
                entity.Status.ReseedAttempts = null;
                if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == ReseedBlockedCondition) is { Status: "True" })
                    ConditionOps.Set(entity.Status.Conditions, ReseedBlockedCondition, "False", "Converged",
                        $"Data movement of '{dbName}' is no longer suspended.", now: _clock.GetUtcNow().UtcDateTime);
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
            else if (seedingReplicas.Count > 0 && thisDbStates.Where(IsNotSynchronizing).All(d => seedingReplicas.Contains(d.ReplicaName)))
            {
                newPhase = Phases.Seeding;
                reason = "AutomaticSeeding";
                message = $"Automatic seeding of '{dbName}' to {string.Join(", ", seedingReplicas)} in progress";
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

    private static bool IsNotSynchronizing(AgDatabaseStateInfo d) =>
        d.SynchronizationState.Equals("NOT SYNCHRONIZING", StringComparison.OrdinalIgnoreCase)
        || d.SynchronizationState.Equals("NOT_SYNCHRONIZING", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The approval annotation stayed after use — the next data-loss failover would have
    /// rolled back its divergent replicas' commits without anyone being asked. It is consumed into
    /// the status, bound to the failover it approves; a later data-loss failover asks again.
    /// </summary>
    /// The approval names the divergences it settles (ids from the AG's openDivergences,
    /// comma-separated); "true" settles the most recent one only. Returns the ids settled now; the
    /// settled ids still open on the AG are kept in status.resolvedDivergences.
    private async Task<List<string>> ConsumeDataLossApprovalAsync(SqlAvailabilityGroupDatabase entity,
        IReadOnlyList<AgDivergenceStatus> divergences, CancellationToken ct)
    {
        var status = entity.Status!;
        var open = divergences.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var resolved = (status.ResolvedDivergences ?? []).Where(open.Contains).ToList();
        var approvedNow = new List<string>();
        if (entity.Metadata.Annotations?.TryGetValue(ApproveDataLossResumeAnnotation, out var value) == true
            && !string.IsNullOrWhiteSpace(value) && divergences.Count > 0)
        {
            var requested = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                ? divergences.OrderByDescending(d => d.At).Take(1).Select(d => d.Id).ToList()
                : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            approvedNow = requested.Where(open.Contains).Except(resolved).ToList();
            try
            {
                // A patch of the annotation only — the PUT wrote the spec back as read.
                await kubernetesClient.PatchMetadataAsync(entity,
                    new Dictionary<string, string?> { [ApproveDataLossResumeAnnotation] = null }, ct);
                resolved.AddRange(approvedNow);
                var unknown = requested.Where(id => !open.Contains(id)).ToList();
                if (unknown.Count > 0)
                    await eventPublisher.PublishAsync(entity, "DataLossApprovalIgnored",
                        $"{ApproveDataLossResumeAnnotation} names no open divergence of the AG for {string.Join(", ", unknown)}: " +
                        "nothing was approved for them.", "Warning", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Not consumed: it is not recorded either, and is read again on the next pass.
                logger.LogWarning(ex, "Could not consume the {Annotation} annotation; it is read again on the next pass", ApproveDataLossResumeAnnotation);
                approvedNow = [];
            }
        }
        status.ResolvedDivergences = resolved.Count > 0 ? resolved : null;
        return approvedNow;
    }

    /// <summary>A divergent copy that was re-seeded holds the discarded commits no more: the
    /// divergences it came from are settled for this database.</summary>
    /// <summary>
    /// The open divergences of the AG for this database — the status's and the markers the
    /// database carries on its primary, which survive a recreated CR. A status divergence the
    /// database does not carry yet is written there, which migrates it.
    /// </summary>
    private async Task<IReadOnlyList<AgDivergenceStatus>> DivergencesAsync(SqlAvailabilityGroup? ag,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string dbName, IReadOnlyCollection<string> resolved, CancellationToken ct)
    {
        var fromStatus = AgDivergences.Open(ag);
        IReadOnlyList<DivergenceMarker> markers;
        try
        {
            markers = await DivergenceMarkers.ReadAsync(agManager, primaryConn, dbName, ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            logger.LogDebug(ex, "Could not read the divergence markers of {Db}", dbName);
            return fromStatus;
        }
        // A divergence this AGD already settled stays settled, while the AG status still lists it.
        var missing = fromStatus.Where(d => markers.All(m => m.Id != d.Id) && !resolved.Contains(d.Id)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                var next = missing.Aggregate(markers, (all, d) => DivergenceMarkers.With(all, DivergenceMarkers.Of(d, dbName, 0)));
                await DivergenceMarkers.WriteAsync(agManager, primaryConn, dbName, next.ToList(), ct);
                markers = next;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                logger.LogDebug(ex, "Could not write the divergence markers of {Db}", dbName);
            }
        }
        return fromStatus.Concat(markers.Where(m => fromStatus.All(d => d.Id != m.Id)).Select(m => DivergenceMarkers.ToStatus(m, dbName))).ToList();
    }

    /// <summary>An approved or re-seeded divergence leaves the database's markers.</summary>
    private async Task ForgetMarkersAsync(Microsoft.Data.SqlClient.SqlConnection primaryConn, string dbName, IReadOnlyCollection<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        try
        {
            var markers = await DivergenceMarkers.ReadAsync(agManager, primaryConn, dbName, ct);
            if (markers.Any(m => ids.Contains(m.Id)))
                await DivergenceMarkers.WriteAsync(agManager, primaryConn, dbName, DivergenceMarkers.Without(markers, ids).ToList(), ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            logger.LogWarning(ex, "Could not remove settled divergences {Ids} from the markers of {Db}", string.Join(",", ids), dbName);
        }
    }

    private static void SettleDivergencesOf(SqlAvailabilityGroupDatabase entity, SqlAvailabilityGroup? ag, string replica)
    {
        var settled = AgDivergences.Open(ag).Where(d => replica.Equals(d.From, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToList();
        if (settled.Count == 0)
            return;
        var resolved = entity.Status!.ResolvedDivergences ?? [];
        resolved.AddRange(settled.Except(resolved));
        entity.Status.ResolvedDivergences = resolved;
    }

    /// <summary>Annotation naming a secondary whose copy of the database is dropped and seeded again
    /// Consumed; an explicit human decision, so it applies even to a divergent copy.</summary>
    public const string ReseedReplicaAnnotation = "mssql-operator.evariops.com/reseed-replica";

    /// <summary>Fruitless rounds of RESUME attempts (5 each, 5 minutes apart) before a stuck replica is
    /// re-seeded automatically.</summary>
    private const int ReseedAfterFruitlessRounds = 2;

    /// <summary>An automatic re-seed of the same replica is not repeated within this window.</summary>
    private static readonly TimeSpan ReseedCooldown = TimeSpan.FromHours(1);

    private const string ReseedBlockedCondition = "ReseedBlocked";

    private async Task<SqlAvailabilityGroupDatabase?> ConsumeReseedRequestAsync(SqlAvailabilityGroupDatabase entity, SqlAvailabilityGroup ag,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string saPassword, string headlessService, string ns, string agName,
        string dbName, List<AgDatabaseStateInfo> states, CancellationToken ct)
    {
        if (entity.Metadata.Annotations?.TryGetValue(ReseedReplicaAnnotation, out var replica) != true || string.IsNullOrWhiteSpace(replica))
            return null;
        // In Observe or Plan the request stays, untouched, for when the operator may act.
        if (!Gateway.Acts(entity))
            return null;

        try
        {
            // A patch of the annotation only — the PUT wrote the spec back as read.
            await kubernetesClient.PatchMetadataAsync(entity, new Dictionary<string, string?> { [ReseedReplicaAnnotation] = null }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Not consumed: acting now could repeat it on every pass.
            logger.LogWarning(ex, "Could not consume the {Annotation} annotation; the re-seed waits", ReseedReplicaAnnotation);
            return null;
        }

        if (!states.Any(d => d.ReplicaName.Equals(replica, StringComparison.OrdinalIgnoreCase)
                             && !d.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)))
        {
            await eventPublisher.PublishAsync(entity, "ReseedRejected",
                $"{ReseedReplicaAnnotation}={replica}: not a secondary replica of AG '{agName}' holding '{dbName}'; nothing was dropped.",
                "Warning", ct);
            return null;
        }

        return await ReseedAsync(entity, primaryConn, saPassword, headlessService, ns, agName, dbName, replica,
            $"requested with the {ReseedReplicaAnnotation} annotation", ct, ag: ag);
    }

    /// <summary>
    /// A replica that stays NOT SYNCHRONIZING through two rounds of RESUME attempts
    /// (about ten minutes) is re-seeded. Its copy holds nothing the primary lacks — unless it diverged
    /// after a data-loss failover, which stays behind the approval. Never during a failover or a
    /// rolling update, never twice within the hour for the same replica, and only when the replica
    /// has room for the copy; anything that blocks it is reported (ReseedBlocked, NeedsAttention).
    /// </summary>
    private async Task<SqlAvailabilityGroupDatabase?> TryAutoReseedAsync(SqlAvailabilityGroupDatabase entity, SqlAvailabilityGroup ag,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string saPassword, string headlessService, string ns, string agName,
        string dbName, List<string> stalled, decimal? divergenceFork, CancellationToken ct)
    {
        if (ag.Status?.Phase == Phases.FailingOver || ag.Status?.RollingUpdate?.Phase is not (null or RollingUpdatePhases.Idle))
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        if (!string.Equals(entity.Spec.SeedingMode, "Automatic", StringComparison.OrdinalIgnoreCase))
            return await BlockReseedAsync(entity, $"Replica(s) {string.Join(", ", stalled)} of '{dbName}' do not synchronize and need a " +
                "re-seed, but the seeding mode is Manual: restore a backup there, or switch seedingMode to Automatic.", ct);

        var cooled = stalled.Where(r => entity.Status!.ReseededAt?.GetValueOrDefault(r) is not { } at || now - at >= ReseedCooldown).ToList();
        if (cooled.Count == 0)
            return await BlockReseedAsync(entity, $"Replica(s) {string.Join(", ", stalled)} of '{dbName}' were re-seeded less than an hour " +
                "ago and still do not synchronize: something else is wrong (disk, network, version). Inspect them; the " +
                $"{ReseedReplicaAnnotation} annotation forces a new re-seed.", ct);

        var primarySpace = await agManager.GetDatabaseFileSpaceAsync(primaryConn, dbName, ct);
        var forkLsn = divergenceFork;
        foreach (var replica in cooled)
        {
            await using var replicaConn = await connectionFactory.CreateProbeConnectionAsync(
                $"{replica}.{headlessService}.{ns}.svc.cluster.local", SqlEndpoints.ContainerPort, saPassword, "master", ct);
            if (!string.Equals(await agManager.GetLocalReplicaRoleAsync(replicaConn, agName, ct), "SECONDARY", StringComparison.OrdinalIgnoreCase))
                continue;
            if (forkLsn is { } fork
                && (await agManager.GetDatabaseStatesAsync(replicaConn, agName, ct)).FirstOrDefault(d =>
                    d.ReplicaName.Equals(replica, StringComparison.OrdinalIgnoreCase)
                    && d.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase)) is { LastHardenedLsn: { } lsn }
                && lsn > fork)
                continue; // divergent: the approval decides, not the operator

            List<DatabaseFileSpace> replicaSpace;
            try
            {
                replicaSpace = await agManager.GetDatabaseFileSpaceAsync(replicaConn, dbName, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A copy whose recovery was interrupted (35286/35273) kills the
                // session that reads its files (596). The space check failed on every pass and the
                // re-seed it guards never came — for the very copy that needs one. Unreadable, it is
                // dropped; the volume is checked against its peers anyway.
                logger.LogWarning(ex, "The files of {Db} on {Replica} cannot be read; re-seeding it without the space check", dbName, replica);
                replicaSpace = [];
            }
            var tooSmall = primarySpace.FirstOrDefault(p => replicaSpace.FirstOrDefault(r => r.Type == p.Type) is { } r
                                                          && p.SizeBytes > r.AvailableBytes + r.SizeBytes);
            if (tooSmall is not null)
                return await BlockReseedAsync(entity, $"Replica {replica} of '{dbName}' needs a re-seed, but its " +
                    $"{(tooSmall.Type == 0 ? "data" : "log")} volume cannot hold the copy ({tooSmall.SizeBytes / 1048576} MB needed): grow it.", ct);
            if (await VolumeTooSmallAsync(primarySpace, ns, dbName, replica, ct) is { } shortage)
                return await BlockReseedAsync(entity, shortage, ct);

            return await ReseedAsync(entity, primaryConn, saPassword, headlessService, ns, agName, dbName, replica,
                $"it did not synchronize through {entity.Status!.FruitlessResumeRounds} rounds of RESUME attempts", ct, ag: ag);
        }
        return null;
    }

    /// <summary>How long after a re-seed its seeding may not show yet (a fraction of a second).</summary>
    private static readonly TimeSpan ReseedStartGrace = TimeSpan.FromMinutes(1);

    /// <summary>How long a seeding of the database is followed before it counts as stalled: half an
    /// hour, plus its size at 20 MB/s (4.5 GB took 15 to 40 s on a test cluster).</summary>
    private async Task<TimeSpan> SeedingBoundAsync(Microsoft.Data.SqlClient.SqlConnection primaryConn, string dbName, CancellationToken ct)
    {
        var bytes = (await agManager.GetDatabaseFileSpaceAsync(primaryConn, dbName, ct)).Sum(f => f.SizeBytes);
        return TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(bytes / (20L << 20));
    }

    /// <summary>Seeding attempts of one re-seed before it is reported as blocked.</summary>
    private const int MaxSeedingAttempts = 5;

    /// <summary>
    /// The first re-seed on a test cluster failed with error 1412 — the per-minute log backup ran
    /// between the seeding backup and the join, and once its copy is taken out of the AG the primary
    /// no longer holds the log for that replica. Started a few seconds later, it went through. A
    /// re-seed whose seeding failed is started again, a few times, before it is reported.
    /// </summary>
    private async Task<SqlAvailabilityGroupDatabase?> RetryFailedSeedingAsync(SqlAvailabilityGroupDatabase entity,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string saPassword, string headlessService, string ns, string agName,
        string dbName, List<AgDatabaseStateInfo> states, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (replica, at) in (entity.Status!.ReseededAt ?? []).ToList())
        {
            if (now - at > ReseedCooldown
                || states.Any(d => d.ReplicaName.Equals(replica, StringComparison.OrdinalIgnoreCase)
                                   && d.SynchronizationState is "SYNCHRONIZED" or "SYNCHRONIZING"))
                continue;
            if (await agManager.GetReplicaSeedingAsync(primaryConn, agName, dbName, replica, ct) is not { CurrentState: "FAILED" } seeding
                || seeding.StartTime < at.AddMinutes(-1))
                continue;

            // A copy that cannot fit fails every attempt, each one after filling the volume.
            if (await VolumeTooSmallAsync(await agManager.GetDatabaseFileSpaceAsync(primaryConn, dbName, ct), ns, dbName, replica, ct) is { } shortage)
            {
                if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == ReseedBlockedCondition) is { Status: "True" } blocked
                    && blocked.Message == shortage)
                    continue;
                return await BlockReseedAsync(entity, shortage, ct);
            }

            var attempts = entity.Status.ReseedAttempts?.GetValueOrDefault(replica, 1) ?? 1;
            if (attempts >= MaxSeedingAttempts)
            {
                if (entity.Status.Conditions?.FirstOrDefault(c => c.Type == ReseedBlockedCondition) is { Status: "True" })
                    continue;
                return await BlockReseedAsync(entity, $"The re-seed of '{dbName}' on {replica} failed {attempts} times " +
                    $"({(string.IsNullOrEmpty(seeding.FailureState) ? "no reason reported" : seeding.FailureState)}). Look at the error log of " +
                    $"{replica}: error 1412 means a log backup ran between the seeding and the join — pause the log backups during a new " +
                    $"attempt ({ReseedReplicaAnnotation}), or restore a backup there; error 41145 (\"already joined\") means its SQL Server " +
                    $"still holds the membership of the dropped copy — restart it (delete pod {replica}), then annotate " +
                    $"{ReseedReplicaAnnotation}={replica}; error 5149 or OS error 31 means its volume is too small.", ct);
            }
            return await ReseedAsync(entity, primaryConn, saPassword, headlessService, ns, agName, dbName, replica,
                $"its seeding failed ({(string.IsNullOrEmpty(seeding.FailureState) ? "no reason reported" : seeding.FailureState)}); attempt {attempts + 1} of {MaxSeedingAttempts}",
                ct, retry: true);
        }
        return null;
    }

    /// <summary>
    /// The new volume of a replica that lost its own holds no file of the
    /// database, so SQL Server reports no free space for it and the check above saw nothing. The copy
    /// filled the 8Gi volume (OS error 31) at each of the five attempts, reported as a log backup race
    /// (1412). Obvious shortages are read from the volume itself. The reason, or null.
    /// </summary>
    private async Task<string?> VolumeTooSmallAsync(List<DatabaseFileSpace> primarySpace, string ns, string dbName, string replica,
        CancellationToken ct)
    {
        foreach (var file in primarySpace)
        {
            var template = file.Type == 0 ? "data" : "log";
            if (await kubernetesClient.GetAsync<V1PersistentVolumeClaim>($"{template}-{replica}", ns, ct) is { Status.Capacity: { } capacity }
                && capacity.TryGetValue("storage", out var size) && (long)size.ToDecimal() < file.SizeBytes)
                return $"Replica {replica} of '{dbName}' needs a re-seed, but its {template} volume ({size}) cannot hold the copy " +
                       $"({file.SizeBytes / 1048576} MB needed): grow it.";
        }
        return null;
    }

    private async Task<SqlAvailabilityGroupDatabase> BlockReseedAsync(SqlAvailabilityGroupDatabase entity, string message, CancellationToken ct)
    {
        await eventPublisher.PublishAsync(entity, ReseedBlockedCondition, message, "Warning", ct);
        ConditionOps.Set(entity.Status!.Conditions ??= [], ReseedBlockedCondition, "True", ReseedBlockedCondition, message,
            now: _clock.GetUtcNow().UtcDateTime);
        return await UpdatePhase(entity, entity.Status.Phase, ReseedBlockedCondition, message, ct);
    }

    private async Task<SqlAvailabilityGroupDatabase> ReseedAsync(SqlAvailabilityGroupDatabase entity,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string saPassword, string headlessService, string ns, string agName,
        string dbName, string replica, string why, CancellationToken ct, bool retry = false, SqlAvailabilityGroup? ag = null)
    {
        using var operation = MsSqlOperator.Services.Metrics.OperatorTracing.Start(logger, "reseed", $"{ns}/{agName}/{dbName}", ("replica", replica), ("why", why));
        var attempts = entity.Status!.ReseedAttempts ??= [];
        attempts[replica] = retry ? attempts.GetValueOrDefault(replica, 1) + 1 : 1;
        try
        {
            await using var replicaConn = await connectionFactory.CreateProbeConnectionAsync(
                $"{replica}.{headlessService}.{ns}.svc.cluster.local", SqlEndpoints.ContainerPort, saPassword, "master", ct);
            await Gateway.DropSecondaryCopyAsync(entity, agManager, replicaConn, dbName, replica, ct, agScope: agName);
            SettleDivergencesOf(entity, ag, replica);
            // The re-seeded replica no longer holds the divergent commits.
            await ForgetMarkersAsync(primaryConn, dbName,
                (await DivergencesAsync(ag, primaryConn, dbName, entity.Status!.ResolvedDivergences ?? [], ct)).Where(d => replica.Equals(d.From, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToList(), ct);
        }
        catch (ActionWithheldException ex)
        {
            // The gateway did not let the copy be dropped (Observe or Plan mode).
            return await BlockReseedAsync(entity, $"The re-seed of '{dbName}' on {replica} is withheld: {ex.Decision.Reason}.", ct);
        }
        catch (SecondaryCopyNotDroppedException ex)
        {
            // The copy may hold data of its own; a human decides what becomes of it.
            await eventPublisher.PublishAsync(entity, "SecondaryCopyNotDropped", $"Replica {replica}: {ex.Message}", "Warning", ct);
            return await BlockReseedAsync(entity, $"Replica {replica} needs a re-seed of '{dbName}', but its copy was not " +
                "dropped: it is online and writable outside the availability group and may hold data the primary does not have. " +
                "Back it up or drop it by hand; the operator then seeds it again.", ct);
        }
        // The primary died while it seeded this replica; the new one took the partial copy
        // for a seeding to catch up (CATCHUP) and kept it so once the copy was dropped: SEEDING_MODE =
        // AUTOMATIC alone did nothing, the replica never got the database again. MANUAL first cancels
        // it — unless another database seeds to that replica, which it would cancel too.
        var running = await agManager.GetRunningSeedingsAsync(primaryConn, agName, replica, ct);
        await agManager.RequestSeedingAsync(primaryConn, agName, replica, ct,
            cancelRunning: running.Count > 0 && running.All(d => d.Equals(dbName, StringComparison.OrdinalIgnoreCase)));

        var status = entity.Status!;
        var now = _clock.GetUtcNow().UtcDateTime;
        (status.ReseededAt ??= [])[replica] = now;
        status.ConsecutiveResumeAttempts = 0;
        status.FruitlessResumeRounds = 0;
        var conditions = status.Conditions ??= [];
        if (conditions.FirstOrDefault(c => c.Type == ReseedBlockedCondition) is { Status: "True" })
            ConditionOps.Set(conditions, ReseedBlockedCondition, "False", "Reseeding", $"Replica {replica} is being re-seeded.", now: now);
        if (conditions.FirstOrDefault(c => c.Type == ResumeIneffectiveCondition) is { Status: "True" })
            ConditionOps.Set(conditions, ResumeIneffectiveCondition, "False", "Reseeding", $"Replica {replica} is being re-seeded.", now: now);

        var message = $"Replica {replica} dropped its copy of '{dbName}' ({why}) and the primary was asked to seed it again; " +
                      "automatic seeding copies the database from the primary.";
        await eventPublisher.PublishAsync(entity, "ReplicaReseeded", message, "Warning", ct);
        logger.LogWarning("Re-seeding {Db} on replica {Replica} of AG {Ag}: {Why}", dbName, replica, agName, why);
        return await UpdatePhase(entity, Phases.Synchronizing, "Reseeding", message, ct);
    }

    private async Task<LogUsageInfo?> ReadLogUsageAsync(Microsoft.Data.SqlClient.SqlConnection primaryConn, string dbName, CancellationToken ct)
    {
        try
        {
            return await agManager.GetLogUsageAsync(primaryConn, dbName, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read the log usage of {Db}", dbName);
            return null;
        }
    }

    /// <summary>
    /// A burst filled a 4 GB log from 41% to 99% between two 30 s passes, and a
    /// write failed with 9002 before the operator's backup started. Once the log is a quarter of the
    /// way to its backup threshold, it is looked at every few seconds.
    /// </summary>
    private bool _watchLog;

    private static readonly TimeSpan LogWatchInterval = TimeSpan.FromSeconds(5);

    /// <summary>Where the operator's log backups go, on the backup volume of the replica that takes them.</summary>
    internal const string OperatorLogBackupFolder = "/var/opt/mssql/backup/mssql-operator";

    /// <summary>
    /// The operator starts the log chain itself, and nothing backed the log up — it
    /// filled and every write failed with 9002. By default it now backs the log up when it
    /// is half full and nothing else truncated it (log_reuse_wait LOG_BACKUP): another tool that backs
    /// it up often enough keeps it from ever acting. Its files are kept for retentionHours, and only
    /// its own folder is ever cleaned. True when a backup was taken.
    /// </summary>
    private async Task<bool> BackUpLogUnderPressureAsync(SqlAvailabilityGroupDatabase entity, SqlAvailabilityGroup ag,
        Microsoft.Data.SqlClient.SqlConnection primaryConn, string dbName, LogUsageInfo usage, CancellationToken ct)
    {
        var policy = entity.Spec.LogBackup ?? new AgDatabaseLogBackupSpec();
        if (!BacksUpLocally(policy) || usage.PercentUsed < policy.ThresholdPercent
            || !string.Equals(usage.ReuseWait, "LOG_BACKUP", StringComparison.OrdinalIgnoreCase))
            return false;
        var retention = RetentionOf(policy);

        var now = _clock.GetUtcNow().UtcDateTime;
        string file;
        try
        {
            file = await agManager.BackupLogAsync(primaryConn, dbName, OperatorLogBackupFolder, now, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Log backup of {Db} failed", dbName);
            // The backup volume of the primary filled up; it is grown like the data and
            // log volumes, and the next pass, a few seconds later, backs up again.
            var volume = ex is BackupWriteFailedException && volumeAutoGrower is not null && ag.Status?.PrimaryReplica is { } primary
                ? $" On {primary}, {await volumeAutoGrower.GrowBackupVolumeAsync(ag, primary, ct)}."
                : "";
            await eventPublisher.PublishAsync(entity, "LogBackupFailed",
                $"The log of '{dbName}' is {usage.PercentUsed}% full and nothing else truncates it, but the operator's log backup into " +
                $"{OperatorLogBackupFolder} failed: {ex.Message}.{volume} Once the log is full, every write fails with error 9002.", "Warning", ct);
            return false;
        }

        // The operator now holds part of the log chain — said in an event, a condition and a metric.
        entity.Status!.OperatorLogBackupAt = now;
        metrics?.OperatorLogBackup(entity.Metadata.NamespaceProperty, dbName);
        await eventPublisher.PublishAsync(entity, "LogBackedUp",
            $"The log of '{dbName}' was {usage.PercentUsed}% full and nothing else truncated it: backed up to {file} on " +
            $"{primaryConn.DataSource}. Restores need these files; " +
            (retention is { } hours
                ? $"the operator deletes them after {hours} h (from {now.AddHours(hours):O} for this one). "
                : "the operator never deletes them (logBackup.retentionHours is not set). ") +
            "Set logBackup.mode to Disabled when your own tooling backs up the log.", ct: ct);
        // The files are part of the log chain — deleted only within a retention.
        if (retention is not { } kept)
            return true;
        try
        {
            await agManager.DeleteOldBackupsAsync(primaryConn, OperatorLogBackupFolder, "trn", now.AddHours(-kept), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not delete the operator's log backups older than {Hours} h", kept);
        }
        return true;
    }

    private const string LogChainHeldByOperatorCondition = "LogChainHeldByOperator";

    /// <summary>The operator backs up the log itself — Local, or the deprecated Auto and an
    /// unset mode, which keep that behaviour for one more version.</summary>
    internal static bool BacksUpLocally(AgDatabaseLogBackupSpec policy) => policy.Mode is null or "Auto" or "Local";

    /// <summary>How long the operator keeps its log backups; null when it never deletes them
    /// (Local without retentionHours). The deprecated modes keep the former 24 h.</summary>
    internal static int? RetentionOf(AgDatabaseLogBackupSpec policy)
        => policy.RetentionHours ?? (policy.Mode is null or "Auto" ? 24 : null);

    private const string LogBackupModeDeprecatedCondition = "LogBackupModeDeprecated";

    /// <summary>The reason and message of an unset or Auto mode, or null once it is set. A
    /// SqlAvailabilityGroupDatabase made by a SqlDatabase takes its spec from it: the message names that one.</summary>
    internal static (string Reason, string Message)? LogBackupModeDeprecation(string? mode, string? sqlDatabase)
    {
        if (mode is not (null or "Auto"))
            return null;
        var setting = sqlDatabase is null ? "logBackup.mode" : $"spec.availabilityGroup.logBackup.mode of SqlDatabase '{sqlDatabase}'";
        return (mode is null ? "ModeNotSet" : "AutoDeprecated",
            $"logBackup.mode is {(mode is null ? "not set" : "Auto")}: the operator backs up the log itself (Local, " +
            "files deleted after 24 h) for this version only. The next version defaults to Disabled. Set " +
            $"{setting} to Local to keep this behaviour, or to Disabled when your own tooling backs up the log.");
    }

    /// <summary>An unset or Auto mode works as, and says that the default changes.</summary>
    private async Task ReportLogBackupModeAsync(SqlAvailabilityGroupDatabase entity, List<ConditionStatus> conditions, CancellationToken ct)
    {
        var wasReported = conditions.Any(c => c is { Type: LogBackupModeDeprecatedCondition, Status: "True" });
        string? sqlDatabase = null;
        entity.Metadata.Annotations?.TryGetValue(SqlDatabaseController.ManagedByAnnotation, out sqlDatabase);
        if (LogBackupModeDeprecation(entity.Spec.LogBackup?.Mode, sqlDatabase) is (var reason, var message))
        {
            ConditionOps.Set(conditions, LogBackupModeDeprecatedCondition, "True", reason, message, now: _clock.GetUtcNow().UtcDateTime);
            if (!wasReported)
                await eventPublisher.PublishAsync(entity, "LogBackupModeDeprecated", message, "Warning", ct);
        }
        else if (wasReported)
            ConditionOps.Set(conditions, LogBackupModeDeprecatedCondition, "False", "ModeSet",
                $"logBackup.mode is {entity.Spec.LogBackup?.Mode}.", now: _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>While one of the operator's own log backups is within the retention window, any
    /// restore of the database needs it — a fact nobody saw, now a condition.</summary>
    private void ReportLogChainHolder(SqlAvailabilityGroupDatabase entity, string dbName, AgDatabaseLogBackupSpec policy,
        List<ConditionStatus> conditions)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var retention = RetentionOf(policy);
        if (entity.Status!.OperatorLogBackupAt is { } at && (retention is not { } hours || now - at < TimeSpan.FromHours(hours)))
            ConditionOps.Set(conditions, LogChainHeldByOperatorCondition, "True", "OperatorLogBackups",
                $"The operator backed up the log of '{dbName}' itself (last at {at:O}) into {OperatorLogBackupFolder} on the " +
                "primary of the time: any restore needs those files" +
                (retention is { } h ? $" until they are deleted, {h} h after each backup" : ", which the operator never deletes") +
                ". Set logBackup.mode to Disabled once your own tooling backs up the log.", now: now);
        else if (conditions.FirstOrDefault(c => c.Type == LogChainHeldByOperatorCondition) is { Status: "True" })
            ConditionOps.Set(conditions, LogChainHeldByOperatorCondition, "False", "NoOperatorLogBackup",
                $"No log backup of the operator for '{dbName}' is within the retention window any more.", now: now);
    }

    /// <summary>Log usage from which the database is reported as running out of log.</summary>
    private const int LogFullWarningPercent = 80;

    private const string TransactionLogFullCondition = "TransactionLogFull";

    /// <summary>
    /// The transaction log of the primary filled up (9002) and every write failed for
    /// minutes while the AG said Running and this resource Synchronized — and earlier, a secondary on
    /// blank volumes held the whole log back (AVAILABILITY_REPLICA) with nothing said either. The
    /// usage and what prevents the truncation are reported, with what to do about it.
    /// </summary>
    private async Task ReportLogSpaceAsync(SqlAvailabilityGroupDatabase entity, SqlAvailabilityGroup ag, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        string dbName, List<AgDatabaseStateInfo> states, CancellationToken ct)
    {
        var usage = await ReadLogUsageAsync(primaryConn, dbName, ct);
        if (usage is null)
            return;
        var policy = entity.Spec.LogBackup ?? new AgDatabaseLogBackupSpec();
        _watchLog = BacksUpLocally(policy) && usage.PercentUsed >= policy.ThresholdPercent / 2;
        var backedUp = await BackUpLogUnderPressureAsync(entity, ag, primaryConn, dbName, usage, ct);
        if (backedUp)
            usage = await ReadLogUsageAsync(primaryConn, dbName, ct) ?? usage;

        var conditions = entity.Status!.Conditions ??= [];
        await ReportLogBackupModeAsync(entity, conditions, ct);
        ReportLogChainHolder(entity, dbName, policy, conditions);
        if (usage.PercentUsed < LogFullWarningPercent)
        {
            if (conditions.FirstOrDefault(c => c.Type == TransactionLogFullCondition) is { Status: "True" })
                ConditionOps.Set(conditions, TransactionLogFullCondition, "False", "LogSpaceAvailable",
                    $"The transaction log of '{dbName}' has room again.", now: _clock.GetUtcNow().UtcDateTime);
            return;
        }
        // Right after its own backup the log is not released yet: the secondaries still have to
        // harden what the burst wrote (AVAILABILITY_REPLICA). A test run raised TransactionLogFull, and
        // so NeedsAttention, for the 30 s it took. The next pass, a few seconds later, tells.
        if (backedUp)
            return;

        var lagging = states
            .Where(d => !d.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                        && !d.SynchronizationState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase)
                        && !d.SynchronizationState.Equals("SYNCHRONIZING", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ReplicaName)
            .ToList();
        var autoBackup = BacksUpLocally(entity.Spec.LogBackup ?? new AgDatabaseLogBackupSpec());
        var (reason, advice) = usage.ReuseWait.ToUpperInvariant() switch
        {
            "LOG_BACKUP" when autoBackup => ("LogBackup",
                "the operator's own log backups (logBackup.mode Local) do not keep up or fail: see the LogBackupFailed events (backup volume full or too slow)"),
            "LOG_BACKUP" => ("LogBackup",
                "no log backup has truncated it: schedule BACKUP LOG (for instance a SqlAgentJob targeting the AG), or set logBackup.mode to Local"),
            "AVAILABILITY_REPLICA" => ("AvailabilityReplica",
                $"a secondary has not received or redone it ({(lagging.Count > 0 ? string.Join(", ", lagging) : "a lagging replica")}): " +
                "bring it back, or remove it from the availability group so the log can be truncated"),
            "ACTIVE_TRANSACTION" => ("ActiveTransaction", "a long-running transaction holds it"),
            var other => ("Other", $"log_reuse_wait_desc is {other}"),
        };
        var message = $"The transaction log of '{dbName}' on the primary is at least {LogFullWarningPercent}% full and cannot be truncated: " +
                      $"{advice}. Once it is full, every write fails with error 9002.";
        await eventPublisher.PublishAsync(entity, TransactionLogFullCondition, message, "Warning", ct);
        ConditionOps.Set(conditions, TransactionLogFullCondition, "True", reason, message, now: _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The fork point of the last failover for this database, when it accepted
    /// data loss and its divergence is still pending: a bystander beyond it got that log from the
    /// previous primary. After a later failover, LSNs no longer tell the two timelines apart.</summary>
    private static decimal? BystanderFork(SqlAvailabilityGroup? ag, IReadOnlyList<AgDivergenceStatus> pending, string dbName)
        => pending.FirstOrDefault(d => AgDivergences.IsLastFailover(ag, d)) is { } last ? AgDivergences.ForkLsn(last, dbName) : null;

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

        // Waiting for the backup window or slot, the AG and the database are there.
        var waitingForBackup = reason is WaitingForBackupWindow or WaitingForBackupSlot or InitializationBackupRunning;
        var agReady = phase is not ("Pending" or "WaitingForAg") || waitingForBackup;
        ConditionOps.Set(conditions, "AgReady", agReady ? "True" : "False",
            agReady ? "AgRunning" : reason,
            agReady ? $"AG '{entity.Spec.AvailabilityGroupRef.Name}' is running" : message);

        var dbExists = (!(phase is "Pending" or "WaitingForAg") || waitingForBackup) && reason != "DatabaseNotFound";
        ConditionOps.Set(conditions, "DatabaseExists", dbExists ? "True" : "False",
            dbExists ? "DatabaseFound" : reason,
            dbExists ? $"Database '{entity.Spec.DatabaseName}' exists on primary" : message);

        ConditionOps.Set(conditions, "Ready", phase == "Synchronized" ? "True" : "False", reason, message);

        // A human decision is pending only when a divergent replica waits for the data-loss
        // approval, the log of the primary cannot be truncated, or a stuck replica could not be
        // re-seeded on its own. Everything else heals without anyone.
        if (conditions.FirstOrDefault(c => c.Status == "True" && c.Type is "ManualRejoinRequired" or TransactionLogFullCondition or ReseedBlockedCondition)
            is { } pending)
            NeedsAttention.Require(conditions, pending.Type, pending.Message ?? pending.Type);
        else
            NeedsAttention.Clear(conditions);
        metrics?.SetNeedsAttention("SqlAvailabilityGroupDatabase", entity.Metadata.NamespaceProperty, entity.Metadata.Name,
            NeedsAttention.IsRequired(conditions));

        entity.Status.Conditions = conditions;
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private async Task<string?> ResolveSaPasswordFromAgAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        // Null only on 404; transport errors propagate (runtime backoff).
        return await SecretValues.TryGetAsync(kubernetesClient, ag.Spec.SaPasswordSecretRef, ns, ct);
    }
}
