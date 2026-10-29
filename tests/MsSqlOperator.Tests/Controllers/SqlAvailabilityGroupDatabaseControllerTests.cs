using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupDatabaseControllerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connectionFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly FakeVolumeAutoGrower _volumeAutoGrower = new();
    private readonly SqlAvailabilityGroupDatabaseController _controller;

    public SqlAvailabilityGroupDatabaseControllerTests()
    {
        _controller = new SqlAvailabilityGroupDatabaseController(
            NullLogger<SqlAvailabilityGroupDatabaseController>.Instance, _k8s, _connectionFactory,
            _agManager, _eventPublisher, volumeAutoGrower: _volumeAutoGrower);
        // The only local role this controller reads is the one of a stalled secondary it resumes.
        _agManager.LocalReplicaRole = "SECONDARY";
    }

    private static SqlAvailabilityGroupDatabase CreateEntity(string phase = "Pending") => new()
    {
        Metadata = new V1ObjectMeta { Name = "orders-sagdb", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupDatabaseSpec
        {
            AvailabilityGroupRef = new ResourceReference { Name = "orders-ag" },
            DatabaseName = "OrdersDB",
            SeedingMode = "Automatic"
        },
        Status = new SqlAvailabilityGroupDatabaseStatus { Phase = phase }
    };

    private static SqlAvailabilityGroup CreateAg(string phase = "Running") => new()
    {
        Metadata = new V1ObjectMeta { Name = "orders-ag", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupSpec
        {
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" }
        },
        Status = new SqlAvailabilityGroupStatus
        {
            Phase = phase,
            PrimaryReplica = "orders-ag-0"
        }
    };

    [Fact]
    public async Task ReconcileAsync_PendingWithNoAg_SetsWaitingForAg()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_PendingWithAgNotRunning_SetsWaitingForAg()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet("orders-ag", "default", CreateAg("Initializing"));

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_FailedPhase_ReEvaluatesFromScratch()
    {
        // Failed is not terminal: the controller re-evaluates from scratch so transient
        // errors self-recover. With the AG absent it should fall back to WaitingForAg.
        var entity = CreateEntity("Failed");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
        // Failed re-reconciles: the AG reference is resolved again.
        Assert.Single(_k8s.GetCalls, c => c.Type == "SqlAvailabilityGroup" && c.Name == "orders-ag" && c.Ns == "default");
    }

    [Fact]
    public async Task ReconcileAsync_PendingConditions_HasAgReadyFalse()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var agReadyCondition = result.Entity!.Status!.Conditions!.FirstOrDefault(c => c.Type == "AgReady");
        Assert.NotNull(agReadyCondition);
        Assert.Equal("False", agReadyCondition!.Status);
    }

    [Fact]
    public async Task ReconcileAsync_StalledSecondary_ResumesOnThatSecondaryNotThePrimary()
    {
        // After a failover the former primary lands in NOT SYNCHRONIZING (SUSPEND_FROM_PARTNER).
        // HADR RESUME must be issued ON that secondary — issuing it on the primary connection is a
        // no-op, which left the AG without redundancy after every failover.
        var entity = CreateEntity("Synchronized");
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // The resume connects to the stalled secondary's headless FQDN, not the primary's.
        Assert.Single(_connectionFactory.Calls,
            c => c.Host == "orders-ag-1.orders-ag-headless.default.svc.cluster.local" && c.Port == 1433 && c.Password == "pw" && c.Database == "master");
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
    }

    [Fact]
    public async Task ReconcileAsync_AgDegradedBySuspendedSecondary_StillResumesIt()
    {
        // After a failover the AG is Degraded precisely because the former primary's
        // database is suspended — and this controller, the one that resumes it, waited for Running.
        var entity = CreateEntity("Synchronized");
        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotEqual("WaitingForAg", result.Entity!.Status!.Phase);
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
    }

    [Fact]
    public async Task ReconcileAsync_ResumeDidNotConverge_SuspendsThenResumesTheSecondary()
    {
        // A bystander secondary of a clusterless failover is SUSPEND_FROM_PARTNER on the
        // primary but not suspended locally: RESUME alone is a no-op there.
        var entity = CreateEntity("Synchronizing");
        entity.Status!.ConsecutiveResumeAttempts = 1;
        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-2", "SECONDARY", "NOT SYNCHRONIZING", true, 0, 0),
        ];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.SuspendDataMovementCalls, d => d == "OrdersDB");
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
        // A secondary tied to a demoted, resurrected primary is pointed back first.
        Assert.Single(_agManager.SetSecondaryHosts, h => h.StartsWith("orders-ag-2.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("RESOLVING")]
    [InlineData("PRIMARY")] // resurrected, before the orchestrator demotes it
    public async Task ReconcileAsync_StalledReplicaNotSecondaryYet_IsNotResumedNorCounted(string localRole)
    {
        // The primary lists a former primary still RESOLVING as SECONDARY; the five RESUME
        // attempts were spent on it before it took the secondary role.
        var entity = CreateEntity("Synchronizing");
        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];
        _agManager.LocalReplicaRole = localRole;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Equal(0, result.Entity!.Status!.ConsecutiveResumeAttempts);
    }

    [Fact]
    public async Task ReconcileAsync_StalledSecondaryCutFromThePrimary_IsNotResumedNorCounted()
    {
        // During a HADR partition the five RESUME attempts were spent on
        // secondaries that could not resume, and ResumeIneffective asked for a re-seed as it ended.
        var entity = CreateEntity("Synchronizing");
        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];
        _agManager.LocalReplicaRole = "SECONDARY";
        _agManager.ConnectedToPrimary = false;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Equal(0, result.Entity!.Status!.ConsecutiveResumeAttempts);
    }

    [Theory]
    [InlineData(150, false)] // synchronized the previous primary's divergent log after the fork
    [InlineData(90, true)]   // merely behind the fork point: nothing of its own to lose
    public async Task ReconcileAsync_AfterDataLossFailover_BystanderBeyondTheFork_WaitsForApproval(int localLsn, bool resumed)
    {
        // Chaos-1 followed the resurrected previous primary for seconds after a forced
        // failover and held commits the new primary never had — the gate only covered the former.
        var entity = CreateEntity("Synchronizing");
        var ag = CreateAg("Degraded");
        ag.Status!.LastFailover = new AgLastFailoverStatus
        {
            From = "orders-ag-2",
            To = "orders-ag-0",
            DataLoss = true,
            ForkLsns = new Dictionary<string, string> { ["OrdersDB"] = "100" },
        };
        _k8s.SetGet("orders-ag", "default", ag);
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        var primaryView = new List<AgDatabaseStateInfo>
        {
            new("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        };
        _agManager.DatabaseStatesResolver = c => c.DataSource.StartsWith("orders-ag-1.", StringComparison.Ordinal)
            ? [new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, LastHardenedLsn: localLsn)]
            : primaryView;

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(resumed, _agManager.ResumeDataMovementCalls.Contains("OrdersDB"));
        Assert.Equal(!resumed, _eventPublisher.Reasons.Contains("ManualRejoinRequired"));
    }

    private static readonly DateTime FirstDataLossFailover = new(2026, 10, 27, 7, 26, 3, DateTimeKind.Utc);

    private SqlAvailabilityGroupDatabase AfterDataLossFailover(DateTime failoverAt)
    {
        var entity = CreateEntity("Synchronizing");
        var ag = CreateAg("Degraded");
        ag.Status!.LastFailover = new AgLastFailoverStatus
        {
            From = "orders-ag-2",
            To = "orders-ag-0",
            DataLoss = true,
            Timestamp = failoverAt,
        };
        _k8s.SetGet("orders-ag", "default", ag);
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-2", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, IsSuspended: true),
        ];
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_DataLossRejoinApproved_ConsumesTheApproval()
    {
        // The annotation stayed after use and would have approved the next data-loss
        // failover's rollback as well, without anyone being asked.
        var entity = AfterDataLossFailover(FirstDataLossFailover);
        entity.Metadata.Annotations = new Dictionary<string, string> { [SqlAvailabilityGroupDatabaseController.ApproveDataLossResumeAnnotation] = "true" };

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
        Assert.Contains(_k8s.PatchMetadataCalls, c => c.Annotations.TryGetValue(SqlAvailabilityGroupDatabaseController.ApproveDataLossResumeAnnotation, out var v) && v is null);
        Assert.Empty(_k8s.UpdateCalls.OfType<SqlAvailabilityGroupDatabase>());
        Assert.Equal([AgDivergences.IdOf(FirstDataLossFailover, "orders-ag-0")], result.Entity!.Status!.ResolvedDivergences);
    }

    [Fact]
    public async Task ReconcileAsync_StatusLostByAGitOpsRecreation_TheDatabaseStillBlocksTheDivergentReplica()
    {
        // The AG and the AGD were recreated — their status, and the divergence in it, is
        // gone — but the database carries its marker on the primary.
        var entity = AfterDataLossFailover(FirstDataLossFailover);
        var ag = (await _k8s.GetAsync<SqlAvailabilityGroup>("orders-ag", "default"))!;
        var id = AgDivergences.IdOf(FirstDataLossFailover, "orders-ag-0");
        ag.Status!.LastFailover = null;
        ag.Status.OpenDivergences = [];
        _agManager.DatabaseProperties[("OrdersDB", DivergenceMarkers.PropertyName)] =
            DivergenceMarkers.Serialize([new DivergenceMarker(id, 3, "orders-ag-2", "orders-ag-0", FirstDataLossFailover, null)]);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Contains("ManualRejoinRequired", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_DivergenceOfTheStatus_IsWrittenIntoTheDatabase_AndForgottenOnceApproved()
    {
        // The entries migrate to the markers; an approval removes them.
        var id = AgDivergences.IdOf(FirstDataLossFailover, "orders-ag-0");
        await _controller.ReconcileAsync(AfterDataLossFailover(FirstDataLossFailover), CancellationToken.None);
        Assert.Contains(id, _agManager.DatabaseProperties[("OrdersDB", DivergenceMarkers.PropertyName)]);

        var approved = AfterDataLossFailover(FirstDataLossFailover);
        approved.Metadata.Annotations = new Dictionary<string, string> { [SqlAvailabilityGroupDatabaseController.ApproveDataLossResumeAnnotation] = id };
        await _controller.ReconcileAsync(approved, CancellationToken.None);

        Assert.False(_agManager.DatabaseProperties.ContainsKey(("OrdersDB", DivergenceMarkers.PropertyName)));
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
    }

    [Fact]
    public async Task ReconcileAsync_ApprovalOfAnEarlierFailover_DoesNotCoverANewOne()
    {
        var entity = AfterDataLossFailover(FirstDataLossFailover.AddHours(2));
        entity.Status!.ResolvedDivergences = [AgDivergences.IdOf(FirstDataLossFailover, "orders-ag-0")];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Contains("ManualRejoinRequired", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_ForcedFailoverThenPlannedOne_DivergentReplicaStaysBlocked()
    {
        // Forced A→B, then planned B→C. The single LastFailover slot then named B→C and let A
        // rejoin, losing the commits it alone held, without anyone being asked.
        var entity = AfterDataLossFailover(FirstDataLossFailover);
        var ag = (await _k8s.GetAsync<SqlAvailabilityGroup>("orders-ag", "default"))!;
        ag.Status!.OpenDivergences = [AgDivergences.Of(ag.Status.LastFailover!)];
        ag.Status.LastFailover = new AgLastFailoverStatus
        {
            From = "orders-ag-0",
            To = "orders-ag-1",
            DataLoss = false,
            Timestamp = FirstDataLossFailover.AddHours(1),
        };

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Contains("ManualRejoinRequired", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_ApprovalNamesOneDivergence_DoesNotSettleAnother()
    {
        // The approval carries the id of the divergence it settles.
        var entity = AfterDataLossFailover(FirstDataLossFailover);
        var ag = (await _k8s.GetAsync<SqlAvailabilityGroup>("orders-ag", "default"))!;
        var first = AgDivergences.Of(ag.Status!.LastFailover!);
        var second = new AgDivergenceStatus { Id = "dv-20261027090000-orders-ag-1", From = "orders-ag-2", To = "orders-ag-1", At = FirstDataLossFailover.AddHours(2) };
        ag.Status.OpenDivergences = [first, second];
        entity.Metadata.Annotations = new Dictionary<string, string> { [SqlAvailabilityGroupDatabaseController.ApproveDataLossResumeAnnotation] = first.Id };

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal([first.Id], result.Entity!.Status!.ResolvedDivergences);
        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Contains("ManualRejoinRequired", _eventPublisher.Reasons);
    }

    public static TheoryData<string> TopologyChanges => ["Fencing", "FailoverIntent", "UpdatingPrimary", "FailingOverForUpdate"];

    [Theory]
    [MemberData(nameof(TopologyChanges))]
    public async Task ReconcileAsync_TopologyChanging_NoResumeUntilItEnds(string change)
    {
        // A RESUME issued while the primary moves acts on a primary about to stop being one.
        var entity = CreateEntity("Synchronized");
        var ag = CreateAg("Degraded");
        switch (change)
        {
            case "Fencing": ag.Status!.Fencing = new AgFencingStatus { Pod = "orders-ag-2" }; break;
            case "FailoverIntent": ag.Status!.FailoverIntent = new AgLastFailoverStatus { From = "orders-ag-2", To = "orders-ag-0" }; break;
            default: ag.Status!.RollingUpdate = new AgRollingUpdateStatus { Phase = change }; break;
        }
        _k8s.SetGet("orders-ag", "default", ag);
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Equal("Synchronized", result.Entity!.Status!.Phase);
        Assert.Contains(result.Entity.Status.Conditions!, c => c is { Type: "TopologyChangeInProgress", Status: "True" });

        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        result = await _controller.ReconcileAsync(result.Entity, CancellationToken.None);

        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
        Assert.Contains(result.Entity!.Status!.Conditions!, c => c is { Type: "TopologyChangeInProgress", Status: "False" });
    }

    private SqlAvailabilityGroupDatabase SynchronizedWithLog(int percent, string reuseWait, string? mode = null)
    {
        var entity = CreateEntity("Synchronized");
        if (mode is not null)
            entity.Spec.LogBackup = new AgDatabaseLogBackupSpec { Mode = mode };
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
        ];
        _agManager.LogUsage = new LogUsageInfo(percent, reuseWait);
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_LogHalfFullAndNothingTruncatesIt_TheOperatorBacksItUp()
    {
        // The operator starts the log chain and nothing backed the log up — it filled
        // and every write failed with 9002.
        var clock = new FakeTimeProvider();
        var entity = SynchronizedWithLog(85, "LOG_BACKUP");

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.BackupLogCalls, c => c.Host.StartsWith("orders-ag-0.", StringComparison.Ordinal) && c.Database == "OrdersDB");
        Assert.Single(_agManager.DeleteOldBackupsCalls, c => c.Folder == SqlAvailabilityGroupDatabaseController.OperatorLogBackupFolder
                                                             && c.Cutoff == clock.GetUtcNow().UtcDateTime.AddHours(-24));
        Assert.Contains("LogBackedUp", _eventPublisher.Reasons);
        // Re-read after the backup: the log has room, nothing to report.
        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c.Type == "TransactionLogFull" && c.Status == "True");
    }

    [Fact]
    public async Task ReconcileAsync_OperatorBacksUpTheLog_SaysItNowHoldsTheLogChain()
    {
        // The operator's own log backups are part of the restore chain, and nobody knew.
        var clock = new FakeTimeProvider();
        var entity = SynchronizedWithLog(85, "LOG_BACKUP");
        var metrics = new OperatorMetrics();
        long backups = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == metrics.Meter && instrument.Name == "mssqloperator.operator_log_backups.total")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => backups += value);
        listener.Start();
        var controller = new SqlAvailabilityGroupDatabaseController(NullLogger<SqlAvailabilityGroupDatabaseController>.Instance,
            _k8s, _connectionFactory, _agManager, _eventPublisher, metrics: metrics, timeProvider: clock);

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(clock.GetUtcNow().UtcDateTime, result.Entity!.Status!.OperatorLogBackupAt);
        Assert.Contains(result.Entity.Status.Conditions!, c => c is { Type: "LogChainHeldByOperator", Status: "True" });
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "LogBackedUp" && c.Message.Contains("orders-ag-0")
                                                    && c.Message.Contains(clock.GetUtcNow().UtcDateTime.AddHours(24).ToString("O")));
        Assert.Equal(1, backups);
    }

    [Fact]
    public async Task ReconcileAsync_OperatorLogBackupsAllPastRetention_TheLogChainIsNoLongerHeld()
    {
        var clock = new FakeTimeProvider();
        var entity = SynchronizedWithLog(10, "NOTHING");
        entity.Status!.OperatorLogBackupAt = clock.GetUtcNow().UtcDateTime.AddHours(-25);
        entity.Status.Conditions = [new ConditionStatus { Type = "LogChainHeldByOperator", Status = "True", Reason = "OperatorLogBackups" }];

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Contains(result.Entity!.Status!.Conditions!, c => c is { Type: "LogChainHeldByOperator", Status: "False" });
    }

    [Fact]
    public async Task ReconcileAsync_LogStillHeldRightAfterTheBackup_IsLeftToTheNextPass()
    {
        // After a burst, the secondaries still had to harden what the backup
        // released; TransactionLogFull — and so NeedsAttention — went True for the 30 s it took.
        var entity = SynchronizedWithLog(99, "LOG_BACKUP");
        _agManager.LogUsageAfterBackup = new LogUsageInfo(85, "AVAILABILITY_REPLICA");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.BackupLogCalls);
        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c.Type is "TransactionLogFull" or "NeedsAttention" && c.Status == "True");
        Assert.Equal(TimeSpan.FromSeconds(5), result.RequeueAfter);
    }

    [Theory]
    [InlineData(10, "LOG_BACKUP", null, 30)]   // far from the threshold: the usual pace
    [InlineData(25, "LOG_BACKUP", null, 5)]    // half way to it: watched closely
    [InlineData(40, "LOG_BACKUP", "Disabled", 30)]
    public async Task ReconcileAsync_LogNearingItsBackupThreshold_IsWatchedClosely(int percent, string reuseWait, string? mode, int requeueSeconds)
    {
        // A burst took a 4 GB log from 41% to 99% between two 30 s passes, and a
        // write failed with 9002 before the operator's backup started.
        var entity = SynchronizedWithLog(percent, reuseWait, mode);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.BackupLogCalls);
        Assert.Equal(TimeSpan.FromSeconds(requeueSeconds), result.RequeueAfter);
    }

    [Theory]
    [InlineData(30, "LOG_BACKUP", null)]            // below the threshold
    [InlineData(70, "AVAILABILITY_REPLICA", null)]  // a backup would not truncate it
    [InlineData(70, "LOG_BACKUP", "Disabled")]      // the user's tooling owns the log backups
    public async Task ReconcileAsync_LogBackup_OnlyWhenItHelpsAndIsWanted(int percent, string reuseWait, string? mode)
    {
        var entity = SynchronizedWithLog(percent, reuseWait, mode);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.BackupLogCalls);
    }

    [Fact]
    public async Task ReconcileAsync_OperatorLogBackupFails_IsReportedAndNeedsAttention()
    {
        var entity = SynchronizedWithLog(88, "LOG_BACKUP");
        _agManager.BackupLogFails = true;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Contains("LogBackupFailed", _eventPublisher.Reasons);
        var full = result.Entity!.Status!.Conditions!.Single(c => c.Type == "TransactionLogFull");
        Assert.Equal("True", full.Status);
        Assert.Contains("operator's own log backups", full.Message);
        Assert.Equal("True", result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention").Status);
    }

    [Fact]
    public async Task ReconcileAsync_OperatorLogBackupCannotBeWritten_GrowsThePrimarysBackupVolume()
    {
        // A day of the operator's own log backups filled the 10 GB backup volume; SQL
        // Server reported error 3202 ("31(A device attached to the system is not functioning.)").
        var entity = SynchronizedWithLog(88, "LOG_BACKUP");
        _agManager.BackupLogFails = true;
        _agManager.BackupLogException = new BackupWriteFailedException("Write on \"/var/opt/mssql/backup/mssql-operator/OrdersDB.trn\" failed",
            new IOException("fake"));

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(["orders-ag-0"], _volumeAutoGrower.BackupGrowthCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "LogBackupFailed" && c.Message.Contains("backup-orders-ag-0 from 10Gi to 15Gi"));
    }

    [Fact]
    public async Task ReconcileAsync_LogBackupFailingForAnotherReason_LeavesTheBackupVolumeAlone()
    {
        var entity = SynchronizedWithLog(88, "LOG_BACKUP");
        _agManager.BackupLogFails = true;

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_volumeAutoGrower.BackupGrowthCalls);
    }

    [Fact]
    public async Task ReconcileAsync_LogHeldBackByALaggingSecondary_IsReported()
    {
        // A secondary on blank volumes held the primary's log back (AVAILABILITY_REPLICA)
        // and later the log filled up (9002) while the status said Synchronized.
        var entity = CreateEntity("Synchronized");
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];
        _agManager.LogUsage = new LogUsageInfo(93, "AVAILABILITY_REPLICA");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var condition = result.Entity!.Status!.Conditions!.Single(c => c.Type == "TransactionLogFull");
        Assert.Equal("True", condition.Status);
        Assert.Equal("AvailabilityReplica", condition.Reason);
        Assert.Contains("orders-ag-1", condition.Message);
        Assert.Contains("TransactionLogFull", _eventPublisher.Reasons);

        _agManager.LogUsage = new LogUsageInfo(4, "NOTHING");
        var cleared = await _controller.ReconcileAsync(result.Entity, CancellationToken.None);

        Assert.Equal("False", cleared.Entity!.Status!.Conditions!.Single(c => c.Type == "TransactionLogFull").Status);
    }

    private SqlAvailabilityGroupDatabase StuckThroughSecondRound(int fruitlessRounds = 1)
    {
        // The second round of five RESUME attempts just ended: the condition was Rearmed, not True.
        var entity = CreateEntity("Synchronizing");
        entity.Status!.ConsecutiveResumeAttempts = 5;
        entity.Status.FruitlessResumeRounds = fruitlessRounds;
        entity.Status.Conditions =
        [
            new ConditionStatus { Type = "ResumeIneffective", Status = "False", Reason = "Rearmed" },
            new ConditionStatus { Type = "SuspendedBySpec", Status = "False", Reason = "NotSuspendedBySpec" },
        ];
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, IsSuspended: true),
        ];
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaStuckThroughTwoRounds_IsReseeded()
    {
        // A replica that RESUME could not bring back took a manual re-seed (pause the
        // resource, SET HADR OFF, DROP DATABASE, MODIFY REPLICA … SEEDING_MODE = AUTOMATIC).
        var entity = StuckThroughSecondRound();

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DropSecondaryCopyCalls, c => c.Host.StartsWith("orders-ag-1.", StringComparison.Ordinal) && c.Database == "OrdersDB");
        Assert.Single(_agManager.SeedingRequests, c => c.Host.StartsWith("orders-ag-0.", StringComparison.Ordinal) && c.Replica == "orders-ag-1");
        Assert.Contains("ReplicaReseeded", _eventPublisher.Reasons);
        Assert.True(result.Entity!.Status!.ReseededAt!.ContainsKey("orders-ag-1"));
        Assert.Equal(0, result.Entity.Status.FruitlessResumeRounds);
        Assert.Equal("Synchronizing", result.Entity.Status.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_StuckCopyWhoseFilesCannotBeRead_IsStillReseeded()
    {
        // On a copy whose recovery was interrupted, reading its files killed the
        // session — the space check failed on every pass and the re-seed never came.
        var entity = StuckThroughSecondRound();
        _agManager.FileSpaceResolver = c => c.DataSource!.StartsWith("orders-ag-1.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("596: the session is in the kill state")
            : [];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DropSecondaryCopyCalls, c => c.Host.StartsWith("orders-ag-1.", StringComparison.Ordinal));
        Assert.Contains("ReplicaReseeded", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_ReseedOfAWritableCopy_IsRefused_NeedsAttention()
    {
        // The copy to drop is online and writable outside the AG: the drop refuses it, nothing
        // is seeded over it, and a human decides.
        var entity = StuckThroughSecondRound();
        _agManager.DropSecondaryCopyRefused = c => c.DataSource!.StartsWith("orders-ag-1.", StringComparison.Ordinal);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.SeedingRequests);
        Assert.Contains("SecondaryCopyNotDropped", _eventPublisher.Reasons);
        Assert.Equal("True", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Status);
        Assert.Equal("True", result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention").Status);
    }

    [Fact]
    public async Task ReconcileAsync_ResumeThatThrows_StillCountsAsAnAttempt()
    {
        // The copy taken out of the AG on the replica made every RESUME throw; counted only
        // on success, the attempts never escalated to ResumeIneffective nor to the re-seed.
        var entity = CreateEntity("Synchronizing");
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];
        _agManager.ResumeFails = true;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(1, result.Entity!.Status!.ConsecutiveResumeAttempts);
    }

    private SqlAvailabilityGroupDatabase ReseededMinutesAgo(FakeTimeProvider clock, int attempts, int seedingStartedMinutesAgo)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var entity = CreateEntity("Synchronizing");
        entity.Status!.ReseededAt = new Dictionary<string, DateTime> { ["orders-ag-1"] = now.AddMinutes(-2) };
        entity.Status.ReseedAttempts = new Dictionary<string, int> { ["orders-ag-1"] = attempts };
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];
        _agManager.ReplicaSeedingResolver = r => r == "orders-ag-1"
            ? new ReplicaSeedingInfo(now.AddMinutes(-seedingStartedMinutesAgo), "FAILED", "SQL Error")
            : null;
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_SeedingOfAReseedFailed_IsStartedAgain()
    {
        // The per-minute log backup ran between the seeding and the join (1412); a few
        // seconds later the same re-seed went through.
        var clock = new FakeTimeProvider();
        var entity = ReseededMinutesAgo(clock, attempts: 1, seedingStartedMinutesAgo: 2);

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DropSecondaryCopyCalls, c => c.Host.StartsWith("orders-ag-1.", StringComparison.Ordinal));
        Assert.Single(_agManager.SeedingRequests, c => c.Replica == "orders-ag-1");
        Assert.Equal(2, result.Entity!.Status!.ReseedAttempts!["orders-ag-1"]);
    }

    [Fact]
    public async Task ReconcileAsync_SeedingFailedEveryAttempt_IsReportedNotRetriedForever()
    {
        var clock = new FakeTimeProvider();
        var entity = ReseededMinutesAgo(clock, attempts: 5, seedingStartedMinutesAgo: 2);

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Contains("1412", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Message);
        Assert.Equal("True", result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention").Status);
    }

    [Fact]
    public async Task ReconcileAsync_SeedingFailureOlderThanTheReseed_IsNotRetried()
    {
        var clock = new FakeTimeProvider();
        var entity = ReseededMinutesAgo(clock, attempts: 1, seedingStartedMinutesAgo: 30);

        await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
    }

    [Fact]
    public async Task ReconcileAsync_FirstFruitlessRound_DoesNotReseedYet()
    {
        var entity = StuckThroughSecondRound(fruitlessRounds: 0);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Equal(1, result.Entity!.Status!.FruitlessResumeRounds);
    }

    [Fact]
    public async Task ReconcileAsync_ReseededWithinTheHour_StillStuck_NeedsAttention()
    {
        var clock = new FakeTimeProvider();
        var entity = StuckThroughSecondRound();
        entity.Status!.ReseededAt = new Dictionary<string, DateTime> { ["orders-ag-1"] = clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(10) };

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Equal("True", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Status);
        var attention = result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention");
        Assert.Equal("True", attention.Status);
        Assert.Equal("ReseedBlocked", attention.Reason);
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaVolumeTooSmallForTheCopy_IsNotDropped()
    {
        var entity = StuckThroughSecondRound();
        _agManager.FileSpaceResolver = c => c.DataSource.StartsWith("orders-ag-0.", StringComparison.Ordinal)
            ? [new DatabaseFileSpace(0, 10L << 30, 50L << 30)]
            : [new DatabaseFileSpace(0, 1L << 30, 2L << 30)];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Contains("data volume cannot hold the copy", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Message);
    }

    [Fact]
    public async Task ReconcileAsync_BlankReplicaVolumeTooSmallForTheCopy_IsNotDropped()
    {
        // The copy is gone from the replica, SQL Server reports nothing for it;
        // its volume says it cannot fit.
        var entity = StuckThroughSecondRound();
        _agManager.FileSpaceResolver = c => c.DataSource.StartsWith("orders-ag-0.", StringComparison.Ordinal)
            ? [new DatabaseFileSpace(0, 10L << 30, 50L << 30)]
            : [];
        _k8s.SetGet("data-orders-ag-1", "default", Pvc("8Gi"));

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Contains("data volume (8Gi) cannot hold the copy (10240 MB needed)",
            result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Message);
    }

    [Fact]
    public async Task ReconcileAsync_FailedSeedingIntoAVolumeTooSmall_IsNotRetried()
    {
        // Five attempts each filled the 8Gi volume (OS error 31), reported as error 1412.
        var clock = new FakeTimeProvider();
        var entity = ReseededMinutesAgo(clock, attempts: 1, seedingStartedMinutesAgo: 1);
        _agManager.FileSpaceResolver = _ => [new DatabaseFileSpace(0, 10L << 30, 50L << 30)];
        _k8s.SetGet("data-orders-ag-1", "default", Pvc("8Gi"));

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Contains("data volume (8Gi) cannot hold the copy", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReseedBlocked").Message);
    }

    private static V1PersistentVolumeClaim Pvc(string capacity) => new()
    {
        Status = new V1PersistentVolumeClaimStatus { Capacity = new Dictionary<string, k8s.Models.ResourceQuantity> { ["storage"] = new(capacity) } },
    };

    [Fact]
    public async Task ReconcileAsync_ReseedReplicaAnnotation_IsConsumedAndActedOn()
    {
        var entity = CreateEntity("Synchronized");
        entity.Metadata.Annotations = new Dictionary<string, string> { [SqlAvailabilityGroupDatabaseController.ReseedReplicaAnnotation] = "orders-ag-1" };
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
        ];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // Consumed by a patch of the annotation, the spec untouched.
        Assert.Contains(_k8s.PatchMetadataCalls, c => c.Annotations.TryGetValue(SqlAvailabilityGroupDatabaseController.ReseedReplicaAnnotation, out var v) && v is null);
        Assert.Empty(_k8s.UpdateCalls.OfType<SqlAvailabilityGroupDatabase>());
        Assert.Single(_agManager.DropSecondaryCopyCalls, c => c.Host.StartsWith("orders-ag-1.", StringComparison.Ordinal));
        Assert.Single(_agManager.SeedingRequests, c => c.Replica == "orders-ag-1");
    }

    [Theory]
    [InlineData("Observe")]
    [InlineData("Plan")]
    public async Task ReconcileAsync_ObserveMode_NothingIsDropped_TheRequestStays(string mode)
    {
        // acceptance: in Observe, no SQL or Kubernetes write but the status and the events.
        var entity = CreateEntity("Synchronized");
        entity.Spec.Automation = new AutomationSpec { Mode = mode };
        entity.Metadata.Annotations = new Dictionary<string, string> { [SqlAvailabilityGroupDatabaseController.ReseedReplicaAnnotation] = "orders-ag-1" };
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
        ];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.Empty(_k8s.PatchMetadataCalls);
        Assert.True(entity.Metadata.Annotations.ContainsKey(SqlAvailabilityGroupDatabaseController.ReseedReplicaAnnotation));
    }

    [Fact]
    public async Task ReconcileAsync_DivergentReplicaWithoutApproval_IsNeverReseededAutomatically()
    {
        var entity = StuckThroughSecondRound();
        var ag = CreateAg("Running");
        ag.Status!.LastFailover = new AgLastFailoverStatus
        {
            From = "orders-ag-2",
            To = "orders-ag-0",
            DataLoss = true,
            ForkLsns = new Dictionary<string, string> { ["OrdersDB"] = "100" },
        };
        _k8s.SetGet("orders-ag", "default", ag);
        var primaryView = _agManager.DatabaseStates;
        _agManager.DatabaseStatesResolver = c => c.DataSource.StartsWith("orders-ag-1.", StringComparison.Ordinal)
            ? [new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, LastHardenedLsn: 150)]
            : primaryView;

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DropSecondaryCopyCalls);
    }

    private SqlAvailabilityGroupDatabaseController ControllerAt(FakeTimeProvider clock) => new(
        NullLogger<SqlAvailabilityGroupDatabaseController>.Instance, _k8s, _connectionFactory,
        _agManager, _eventPublisher, timeProvider: clock);

    private SqlAvailabilityGroupDatabase ResumeGivenUpSince(DateTime since)
    {
        var entity = CreateEntity("Suspended");
        entity.Status!.ConsecutiveResumeAttempts = 5;
        entity.Status.Conditions =
        [
            new ConditionStatus { Type = "ResumeIneffective", Status = "True", Reason = "ResumeIneffective", LastTransitionTime = since },
            new ConditionStatus { Type = "SuspendedBySpec", Status = "False", Reason = "NotSuspendedBySpec" },
        ];
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-2", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, IsSuspended: true),
        ];
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_ResumeGivenUpLongAgo_StartsANewRound()
    {
        // The five attempts were spent while the replica was isolated then RESOLVING;
        // once it took the secondary role again, nothing ever resumed it.
        var clock = new FakeTimeProvider();
        var entity = ResumeGivenUpSince(clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(6));

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.SuspendDataMovementCalls, d => d == "OrdersDB");
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
        Assert.Equal(1, result.Entity!.Status!.ConsecutiveResumeAttempts);
        Assert.Equal("False", result.Entity.Status.Conditions!.Single(c => c.Type == "ResumeIneffective").Status);
    }

    [Fact]
    public async Task ReconcileAsync_ResumeJustGivenUp_DoesNotRetryYet()
    {
        var clock = new FakeTimeProvider();
        var entity = ResumeGivenUpSince(clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(1));

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Equal("True", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ResumeIneffective").Status);
    }

    [Theory]
    [InlineData("True", "ResumeIneffective")]
    [InlineData("False", "Rearmed")] // seen on a test cluster: Rearmed stayed after the database synchronized
    public async Task ReconcileAsync_DataMovementConverged_ClearsResumeIneffective(string status, string reason)
    {
        var clock = new FakeTimeProvider();
        var entity = ResumeGivenUpSince(clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));
        var condition = entity.Status!.Conditions!.Single(c => c.Type == "ResumeIneffective");
        condition.Status = status;
        condition.Reason = reason;
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-2", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
        ];

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(0, result.Entity!.Status!.ConsecutiveResumeAttempts);
        var cleared = result.Entity.Status.Conditions!.Single(c => c.Type == "ResumeIneffective");
        Assert.Equal("False", cleared.Status);
        Assert.Equal("Converged", cleared.Reason);
    }

    [Fact]
    public async Task ReconcileAsync_AgDegradedWithoutPrimary_Waits()
    {
        var entity = CreateEntity("Pending");
        var ag = CreateAg("Degraded");
        ag.Status!.PrimaryReplica = null;
        _k8s.SetGet("orders-ag", "default", ag);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_OperatorStartsTheLogChain_SaysLogBackupsAreRequired()
    {
        // The database was FULL already, so no recovery-model warning; once the operator
        // started the chain the log grew until 9002, without a word.
        var entity = CreateEntity("Pending");
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseExistsOnPrimary = true;
        _agManager.RecoveryModel = "FULL";
        _agManager.LogChainEstablished = false;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Contains(result.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupsRequired", Status: "True", Reason: "LogChainStarted" });
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "InitializationBackupTaken" && c.Message.Contains("9002"));
    }

    // ── logBackup.mode transition and purge ──

    [Fact]
    public async Task ReconcileAsync_LocalWithoutRetention_BacksUpButNeverDeletes()
    {
        var clock = new FakeTimeProvider();
        var entity = SynchronizedWithLog(85, "LOG_BACKUP", "Local");

        var result = await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.BackupLogCalls);
        Assert.Empty(_agManager.DeleteOldBackupsCalls);
        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupModeDeprecated", Status: "True" });
    }

    [Fact]
    public async Task ReconcileAsync_LocalWithRetention_DeletesPastIt()
    {
        var clock = new FakeTimeProvider();
        var entity = SynchronizedWithLog(85, "LOG_BACKUP", "Local");
        entity.Spec.LogBackup!.RetentionHours = 48;

        await ControllerAt(clock).ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DeleteOldBackupsCalls, c => c.Cutoff == clock.GetUtcNow().UtcDateTime.AddHours(-48));
    }

    [Theory]
    [InlineData(null, "ModeNotSet")]
    [InlineData("Auto", "AutoDeprecated")]
    public async Task ReconcileAsync_ModeNotSetOrAuto_WorksAsBefore_AndSaysTheDefaultChanges(string? mode, string reason)
    {
        var entity = SynchronizedWithLog(30, "NOTHING", mode);

        var first = await _controller.ReconcileAsync(entity, CancellationToken.None);
        await _controller.ReconcileAsync(first.Entity!, CancellationToken.None);

        Assert.Contains(first.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupModeDeprecated", Status: "True" } && c.Reason == reason);
        Assert.Single(_eventPublisher.Calls, c => c.Reason == "LogBackupModeDeprecated"); // once, not on every pass
    }

    // ── Initialization backup window and slot ──

    private SqlAvailabilityGroupDatabaseController ControllerAt(DateTimeOffset now, MsSqlOperator.Services.ConnectionFactory.TargetLimiter? limiter = null,
        MsSqlOperator.Services.SqlServer.LongOperations? longOperations = null)
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(now);
        return new SqlAvailabilityGroupDatabaseController(NullLogger<SqlAvailabilityGroupDatabaseController>.Instance, _k8s, _connectionFactory,
            _agManager, _eventPublisher, clock, volumeAutoGrower: _volumeAutoGrower, targetLimiter: limiter, longOperations: longOperations);
    }

    // ── The initialization backup out of the pass ──

    private const string BackupKey = "agd-init-backup/default/orders-ag/OrdersDB";

    [Fact]
    public async Task ReconcileAsync_InitializationBackup_AShortOne_TheDatabaseJoinsInTheSamePass()
    {
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations();

        var result = await ControllerAt(Noon, longOperations: operations).ReconcileAsync(WithoutLogChain(), CancellationToken.None);

        Assert.Equal(["OrdersDB"], _agManager.AddDatabaseCalls);
        Assert.Contains("InitializationBackupTaken", _eventPublisher.Reasons);
        Assert.NotEqual("Pending", result.Entity!.Status!.Phase);
        Assert.Null(operations.StateOf(BackupKey));
    }

    [Fact]
    public async Task ReconcileAsync_InitializationBackup_ALongOne_RunsInTheBackground_TheNextPassAddsTheDatabase()
    {
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations(shortWait: TimeSpan.FromMilliseconds(20));
        var controller = ControllerAt(Noon, longOperations: operations);
        _agManager.BackupGate = new TaskCompletionSource();

        var first = await controller.ReconcileAsync(WithoutLogChain(), CancellationToken.None);
        Assert.Equal("Pending", first.Entity!.Status!.Phase);
        _agManager.BackupGate.SetResult();
        await WaitDoneAsync(operations, BackupKey);

        var second = await controller.ReconcileAsync(first.Entity, CancellationToken.None);

        // The backup started the chain, as on the engine: its result is taken all the same — its
        // warning was lost, and the result left for a later database of that name.
        Assert.Equal(["OrdersDB"], _agManager.PrepareDatabaseCalls);
        Assert.Equal(["OrdersDB"], _agManager.AddDatabaseCalls);
        Assert.Contains("InitializationBackupTaken", _eventPublisher.Reasons);
        Assert.Contains(second.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupsRequired", Reason: "LogChainStarted" });
        Assert.Null(operations.StateOf(BackupKey));
    }

    [Fact]
    public async Task ReconcileAsync_BackupTakenOnAnotherCopy_IsTakenAgain_TheDatabaseJoinsAtOnce()
    {
        // The backup ran on a primary that lost its role seconds later (or on a database
        // since dropped); this copy has no log chain. Taken for its own, the result skipped the
        // backup: the join failed with 1475, and only the retry, a minute later, took it.
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations();
        Assert.True(operations.TryStart(BackupKey, _ => Task.FromResult(true)));
        await WaitDoneAsync(operations, BackupKey);

        await ControllerAt(Noon, longOperations: operations).ReconcileAsync(WithoutLogChain(), CancellationToken.None);

        Assert.Equal(["OrdersDB"], _agManager.PrepareDatabaseCalls);
        Assert.Equal(["OrdersDB"], _agManager.AddDatabaseCalls);
        Assert.DoesNotContain("AddDatabaseFailed", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_BackupStillRunning_ItsChainAlreadyThere_TheJoinWaitsForIt()
    {
        // Between its full and its log backup, the chain exists already.
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations();
        var gate = new TaskCompletionSource<bool>();
        Assert.True(operations.TryStart(BackupKey, _ => gate.Task));
        var entity = WithoutLogChain();
        _agManager.LogChainEstablished = true;

        var result = await ControllerAt(Noon, longOperations: operations).ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("Pending", result.Entity!.Status!.Phase);
        Assert.Empty(_agManager.AddDatabaseCalls);
        gate.SetResult(true);
    }

    [Fact]
    public async Task ReconcileAsync_ABackupAlreadyRunsOnTheServer_NoSecondOneIsStarted()
    {
        // An earlier process (or leader) started it; it still runs on the primary.
        _agManager.RunningBackupProgress = 40;
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations();

        var result = await ControllerAt(Noon, longOperations: operations).ReconcileAsync(WithoutLogChain(), CancellationToken.None);

        Assert.Null(operations.StateOf(BackupKey));
        Assert.Equal("Pending", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task LongOperations_OneAtATimePerKey_ResultAndErrorReported()
    {
        var operations = new MsSqlOperator.Services.SqlServer.LongOperations(shortWait: TimeSpan.FromMilliseconds(20));
        var gate = new TaskCompletionSource<bool>();

        Assert.True(operations.TryStart("a", _ => gate.Task));
        Assert.False(operations.TryStart("a", _ => Task.FromResult(false)));
        Assert.True(operations.StateOf("a")!.Running);
        Assert.True((await operations.WaitAsync("a", TestContext.Current.CancellationToken))!.Running); // still running after the short wait
        Assert.Null(await operations.WaitAsync("none", TestContext.Current.CancellationToken));
        gate.SetResult(true);
        await WaitDoneAsync(operations, "a");
        Assert.Equal(true, operations.StateOf("a")!.Result);

        operations.TryStart<bool>("b", _ => throw new InvalidOperationException("disk full"));
        await WaitDoneAsync(operations, "b");
        Assert.Equal("disk full", operations.StateOf("b")!.Error!.Message);
    }

    private static async Task WaitDoneAsync(MsSqlOperator.Services.SqlServer.LongOperations operations, string key)
    {
        for (var i = 0; i < 200 && operations.StateOf(key) is { Running: true }; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    private SqlAvailabilityGroupDatabase WithoutLogChain(string? windowStart = null)
    {
        var entity = CreateEntity("Pending");
        if (windowStart is not null)
            entity.Spec.Initialization = new AgDatabaseInitializationSpec
            {
                BackupWindow = new MaintenanceWindowSpec { Start = windowStart, DurationMinutes = 120 },
            };
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseExistsOnPrimary = true;
        _agManager.RecoveryModel = "FULL";
        _agManager.LogChainEstablished = false;
        return entity;
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReconcileAsync_BackupWindowClosed_TheBackupWaits()
    {
        var result = await ControllerAt(Noon).ReconcileAsync(WithoutLogChain("01:00"), CancellationToken.None);

        Assert.Empty(_agManager.PrepareDatabaseCalls);
        Assert.Equal("Pending", result.Entity!.Status!.Phase);
        Assert.Contains(result.Entity.Status.Conditions!, c => c is { Type: "AgReady", Status: "True" });
    }

    [Fact]
    public async Task ReconcileAsync_BackupWindowOpen_TheBackupIsTaken()
    {
        await ControllerAt(Noon).ReconcileAsync(WithoutLogChain("11:30"), CancellationToken.None);

        Assert.Equal(["OrdersDB"], _agManager.PrepareDatabaseCalls);
    }

    [Fact]
    public async Task ReconcileAsync_LogChainAlreadyThere_NoBackup_TheWindowDoesNotApply()
    {
        var entity = WithoutLogChain("01:00");
        _agManager.LogChainEstablished = true;

        await ControllerAt(Noon).ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(["OrdersDB"], _agManager.PrepareDatabaseCalls);
    }

    [Fact]
    public async Task ReconcileAsync_AnotherBackupRunsOnTheAg_ThisOneWaits()
    {
        var limiter = new MsSqlOperator.Services.ConnectionFactory.TargetLimiter();
        await using var running = limiter.TryAcquire(
            MsSqlOperator.Services.ConnectionFactory.TargetLimiter.BackupKeyOf(TargetKinds.AvailabilityGroup, "default", "orders-ag"), slots: 1);

        var result = await ControllerAt(Noon, limiter).ReconcileAsync(WithoutLogChain(), CancellationToken.None);

        Assert.NotNull(running);
        Assert.Empty(_agManager.PrepareDatabaseCalls);
        Assert.Equal("Pending", result.Entity!.Status!.Phase);
    }

    [Theory]
    [InlineData("01:00", 120, "02:59", true)]
    [InlineData("01:00", 120, "03:00", false)]
    [InlineData("23:00", 120, "00:30", true)]  // across midnight
    [InlineData("23:00", 120, "22:59", false)]
    public void MaintenanceWindow_IsOpen(string start, int minutes, string at, bool open)
    {
        var now = DateTimeOffset.Parse($"2026-10-28T{at}:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(open, MsSqlOperator.Services.SqlServer.MaintenanceWindows.IsOpen(
            new MaintenanceWindowSpec { Start = start, DurationMinutes = minutes }, now));
    }
}
