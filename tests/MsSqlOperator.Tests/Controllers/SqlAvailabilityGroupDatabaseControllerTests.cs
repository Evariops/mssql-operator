using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupDatabaseControllerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connectionFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly SqlAvailabilityGroupDatabaseController _controller;

    public SqlAvailabilityGroupDatabaseControllerTests()
    {
        _controller = new SqlAvailabilityGroupDatabaseController(
            NullLogger<SqlAvailabilityGroupDatabaseController>.Instance, _k8s, _connectionFactory,
            _agManager, _eventPublisher);
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
        Assert.Contains(_k8s.UpdateCalls, e => e is SqlAvailabilityGroupDatabase db
            && db.Metadata.Annotations?.ContainsKey(SqlAvailabilityGroupDatabaseController.ApproveDataLossResumeAnnotation) == false);
        Assert.Equal(FirstDataLossFailover, result.Entity!.Status!.DataLossResumeApprovedFor);
    }

    [Fact]
    public async Task ReconcileAsync_ApprovalOfAnEarlierFailover_DoesNotCoverANewOne()
    {
        var entity = AfterDataLossFailover(FirstDataLossFailover.AddHours(2));
        entity.Status!.DataLossResumeApprovedFor = FirstDataLossFailover;

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.ResumeDataMovementCalls);
        Assert.Contains("ManualRejoinRequired", _eventPublisher.Reasons);
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
}
