using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class AgFailoverOrchestratorTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeAgPodManager _agPodManager = new();
    private readonly FakeReplicaFencer _fencer = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly OperatorMetrics _metrics = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AgFailoverOrchestrator _sut;

    public AgFailoverOrchestratorTests()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));
        // Default: the old primary is reachable and demotable (the graceful path). Tests that model
        // a dead primary make its connection throw and configure the fencer explicitly.
        _sut = new AgFailoverOrchestrator(
            NullLogger<AgFailoverOrchestrator>.Instance, _k8s, _connFactory,
            _agManager, _agPodManager, _fencer, _eventPublisher, _metrics, _time);
    }

    private static SqlAvailabilityGroup CreateAg(
        bool allowDataLoss = false, Dictionary<string, string>? annotations = null) => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = "ag",
                NamespaceProperty = "default",
                Uid = "uid-1",
                Annotations = annotations
            },
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = new AgReplicaTopologySpec { Count = 3 },
                // Threshold 1: unit tests exercise the failover decision itself; the debounce has its own test.
                Failover = new AgFailoverSpec { OperatorManaged = true, AllowAutomaticDataLossFailover = allowDataLoss, PrimaryUnreachableThreshold = 1 }
            },
            Status = new SqlAvailabilityGroupStatus { PrimaryReplica = "ag-0" }
        };

    private static AgReplicaStateInfo Replica(string name, string role, bool ready, string conn = "CONNECTED", string sync = "HEALTHY")
        => new(name, role, sync, conn, "ONLINE", ready, "ALL", "SYNCHRONOUS_COMMIT");

    private static AgHealthReport Health(string? primary, params AgReplicaStateInfo[] replicas)
        => new(primary != null, primary, replicas.ToList(), [], [], replicas.Select(r => r.Name).ToList(), []);

    /// <summary>A secondary that lost its primary, as it reports itself: disconnected from it,
    /// its databases NOT SYNCHRONIZING.</summary>
    private static AgReplicaStateInfo Lost(string name, bool ready = false)
        => Replica(name, "SECONDARY", ready, conn: "DISCONNECTED", sync: "NOT_HEALTHY");

    private static AgDatabaseStateInfo LocalDb(string replica, decimal lsn, string db = "app")
        => new(db, replica, "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, LastHardenedLsn: lsn);

    /// <summary>What the operator recorded while the primary was alive.</summary>
    private static void SynchronizedAtLoss(SqlAvailabilityGroup ag, int requiredSync, params string[] replicas)
    {
        ag.Status!.LastSynchronizedReplicas = [.. replicas];
        ag.Status.LastRequiredSynchronizedSecondaries = requiredSync;
    }

    /// <summary>The unreachable-primary window requires wall-clock time to pass, not
    /// just passes to be counted. Move the fake clock past it.</summary>
    private void ElapseUnreachableWindow() => _time.Advance(TimeSpan.FromSeconds(30));

    /// <summary>Makes the old primary unreachable over SQL, as a dead/partitioned node would be.</summary>
    private void OldPrimaryUnreachable(string pod = "ag-0")
    {
        var previous = _connFactory.ConnectionForHost;
        _connFactory.ConnectionForHost = host => host.StartsWith($"{pod}.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("unreachable")
            : previous(host);
    }

    [Fact]
    public async Task NoAnnotations_WithHealthyPrimary_DoesNothing()
    {
        var ag = CreateAg();
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Empty(_agManager.FailoverCalls);
    }

    [Fact]
    public async Task NoPrimary_SecondaryStillConnectedToIt_PrimaryIsAlive_NoFailover()
    {
        // A secondary still CONNECTED is receiving the primary's log — the primary is up
        // and only the operator cannot reach it. Failing over killed a working primary.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Empty(_fencer.FenceCalls);
        Assert.Null(ag.Status!.PrimaryUnreachableSince);
        Assert.Contains("PrimaryUnreachableFromOperator", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task NoPrimary_ReplicaRefusingTheLogin_DoesNotArmFailover()
    {
        // During an sa rotation the replicas refuse the operator's login — they are alive.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Lost("ag-1")) with { AuthenticationFailedReplicas = ["ag-0"] };

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Equal(0, ag.Status!.PrimaryUnreachableCount);
        Assert.Contains("OperatorLoginRefused", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task NoPrimary_SurvivorWithHighestLsn_EverySynchronizedReplicaAlive_LosslessFailover()
    {
        // The survivors only see themselves as NOT SYNCHRONIZING once the primary is gone; the
        // decision uses what was recorded from the primary and the hardened LSN of each survivor.
        var ag = CreateAg();
        SynchronizedAtLoss(ag, 1, "ag-1", "ag-2");
        var health = new AgHealthReport(false, null, [Lost("ag-1"), Lost("ag-2")],
            [LocalDb("ag-1", 90), LocalDb("ag-2", 120)], [], ["ag-1", "ag-2"], []);
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Equal("ag-2", ag.Status!.PrimaryReplica);
        Assert.False(ag.Status.LastFailover!.DataLoss);
        // The reachable old primary is DEMOTED — never left as a second primary.
        Assert.Single(_agManager.DemoteCalls, a => a == "ag");
    }

    [Fact]
    public async Task NoPrimary_SynchronizedReplicaUnreachable_IsNotProvablyLossless()
    {
        // ag-2 was synchronized and might hold commits ag-1 never received.
        var ag = CreateAg(allowDataLoss: false);
        SynchronizedAtLoss(ag, 1, "ag-1", "ag-2");
        var health = new AgHealthReport(false, null, [Lost("ag-1")], [LocalDb("ag-1", 120)], [], ["ag-1"], []);

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "FailoverNeedsIntervention" && c.Message.Contains("ag-2"));
    }

    [Fact]
    public async Task NoPrimary_RequiredSyncWasZero_IsNotProvablyLossless()
    {
        // At REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT = 0 the primary could commit alone.
        var ag = CreateAg(allowDataLoss: false);
        SynchronizedAtLoss(ag, 0, "ag-1");
        var health = new AgHealthReport(false, null, [Lost("ag-1")], [LocalDb("ag-1", 120)], [], ["ag-1"], []);

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
    }

    [Fact]
    public async Task PrimaryReport_RecordsSynchronizedReplicasAndRequiredSync()
    {
        var ag = CreateAg();
        var health = new AgHealthReport(true, "ag-0",
            [Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", true), Replica("ag-2", "SECONDARY", false, conn: "DISCONNECTED")],
            [
                new("app", "ag-1", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
                new("app", "ag-2", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
            ],
            [], ["ag-0", "ag-1", "ag-2"], [], RequiredSynchronizedSecondaries: 1);

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(["ag-1"], ag.Status!.LastSynchronizedReplicas);
        Assert.Equal(1, ag.Status.LastRequiredSynchronizedSecondaries);
    }

    [Fact]
    public async Task UnreachableOldPrimary_FencingSpansPasses_ThenPromotes()
    {
        // The graceful deletion takes the pod's grace period; waiting for it inside one pass
        // exceeded the pass budget and the promotion never happened.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Lost("ag-1"));
        OldPrimaryUnreachable();
        _fencer.Sequence.Enqueue(MsSqlOperator.Services.Breakout.FenceResult.Pending("graceful deletion requested"));
        _fencer.Result = FakeReplicaFencer.Confirmed;

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var first = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.FencingInProgress, first);
        Assert.Equal("ag-1", ag.Status!.Fencing!.Target);
        Assert.Empty(_agManager.ForceFailoverCalls);

        var second = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, second);
        Assert.Null(ag.Status.Fencing);
        Assert.Equal("ag-1", ag.Status.PrimaryReplica);
    }

    [Fact]
    public async Task FencingFailingBeforeItsFirstStage_LeavesNoRecord_AndTheNextPassRetries()
    {
        // A fencing record persisted before FenceAsync filled it in named no pod: every later pass
        // went to ContinueFencedFailoverAsync with an empty name and threw — failover wedged.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Lost("ag-1"));
        OldPrimaryUnreachable();
        _fencer.Throws = new HttpRequestException("API server unavailable");

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var failed = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, failed);
        Assert.Null(ag.Status!.Fencing);

        _fencer.Throws = null;
        _fencer.Result = FakeReplicaFencer.Confirmed;
        ElapseUnreachableWindow();
        var retried = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, retried);
        Assert.Equal(["ag-0", "ag-0"], _fencer.FenceCalls);
    }

    [Fact]
    public async Task FencingRecordWithoutPod_IsDropped()
    {
        var ag = CreateAg(allowDataLoss: true);
        ag.Status!.Fencing = new AgFencingStatus { Target = "ag-1", AllowDataLoss = true };

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", Health(null, Lost("ag-1")), CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Null(ag.Status.Fencing);
        Assert.Empty(_fencer.FenceCalls);
    }

    [Fact]
    public async Task FencingInProgress_TransientError_KeepsTheRecordForTheNextPass()
    {
        var ag = CreateAg(allowDataLoss: true);
        ag.Status!.Fencing = new AgFencingStatus
        {
            Pod = "ag-0",
            PodUid = "pod-uid",
            Stage = MsSqlOperator.Services.Breakout.ReplicaFencer.GracefulDeletionStage,
            Target = "ag-1",
            AllowDataLoss = true,
        };
        _fencer.Throws = new HttpRequestException("API server unavailable");

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", Health(null, Lost("ag-1")), CancellationToken.None);

        Assert.Equal(FailoverOutcome.FencingInProgress, outcome);
        Assert.NotNull(ag.Status.Fencing);
        Assert.Contains("FailoverFailed", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task FailedPromotion_OldPrimaryAlreadyNotPrimary_IsNotForcedBackIntoTheRole()
    {
        // The previous primary answered as SECONDARY: it was never demoted by this pass, and may be
        // behind. Rolling back onto it ran FORCE_FAILOVER_ALLOW_DATA_LOSS on a lagging copy.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.ForceFailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health(null, Replica("ag-0", "SECONDARY", ready: true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = _ => "SECONDARY";
        _agManager.PromotionFails = c => c.DataSource.StartsWith("ag-1.", StringComparison.Ordinal);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.DoesNotContain(_agManager.PromotionHosts, h => h.StartsWith("ag-0.", StringComparison.Ordinal));
        Assert.DoesNotContain("FailoverRolledBack", _eventPublisher.Reasons);
        Assert.DoesNotContain(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-0" && c.ListenerReady == "writer");
    }

    [Fact]
    public async Task FailedPromotion_OldPrimaryDemotedInThisPass_IsPromotedBack()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";
        _agManager.PromotionFails = c => c.DataSource.StartsWith("ag-1.", StringComparison.Ordinal);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Single(_agManager.DemoteCalls);
        Assert.Contains(_agManager.PromotionHosts, h => h.StartsWith("ag-0.", StringComparison.Ordinal));
        Assert.Contains("FailoverRolledBack", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task Promotion_ReusesTheConnectionAlreadyOpenToTheTarget()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_connFactory.Calls, c => c.Host == "ag-1.ag-headless.default.svc.cluster.local");
    }

    [Fact]
    public async Task PlannedFailover_TargetNotSynchronizedOnThePrimary_AbortsAndRestoresTheWriter()
    {
        // The documented precondition is checked on the primary right before demoting it.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";
        _agManager.DatabaseStatesResolver = _ => [new("app", "ag-1", "SECONDARY", "SYNCHRONIZING", false, 10, 0)];

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Empty(_agManager.DemoteCalls);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Contains("WriterRestored", _eventPublisher.Reasons);
        Assert.Contains(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-0" && c.ListenerReady == "writer");
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossDisallowed_AwaitsManualIntervention()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Lost("ag-1"));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossAllowed_ForcesFailoverAfterFencingTheDeadPrimary()
    {
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Lost("ag-1"));
        OldPrimaryUnreachable();                       // dead node: demotion impossible
        _fencer.Result = FakeReplicaFencer.Confirmed;  // volumes detached → provably cannot write

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, outcome);
        Assert.Single(_agManager.ForceFailoverCalls, a => a == "ag");
        Assert.Contains("ag-0", _fencer.FenceCalls);
        Assert.True(ag.Status!.LastFailover!.DataLoss);
    }

    [Fact]
    public async Task UnreachableOldPrimary_FencingNotConfirmed_AbortsWithoutPromotion()
    {
        // The previous code promoted anyway on the "lossless" path (no fencing at
        // all) and treated "the Pod object left etcd" as proof on the forced path. Neither proves the
        // engine stopped: promoting here is a guaranteed double primary.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Lost("ag-1", ready: true));
        OldPrimaryUnreachable();
        _fencer.Result = FenceResultNotConfirmed();

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "FailoverAborted" && c.Type == "Warning");
    }

    private static MsSqlOperator.Services.Breakout.FenceResult FenceResultNotConfirmed() =>
        MsSqlOperator.Services.Breakout.FenceResult.No("CSI driver does not use VolumeAttachments");

    [Fact]
    public async Task RollingUpdateRequest_InTheStatus_IsExecutedAndConsumed()
    {
        // The rolling update no longer writes the failover-to annotation of the user's CR.
        var ag = CreateAg();
        ag.Status = new SqlAvailabilityGroupStatus { RollingUpdate = new AgRollingUpdateStatus { FailoverTarget = "ag-1" } };
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls);
        Assert.Null(ag.Status.RollingUpdate.FailoverTarget);
    }

    [Fact]
    public async Task PlannedFailover_DemotesOldPrimary_BeforeIssuingTheFailover()
    {
        // This is the rolling-update path too (failoverBeforePrimaryUpdate defaults to
        // true), so it used to double-primary the AG on every image change.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.DemoteCalls);
        Assert.Single(_agManager.FailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "PrimaryDemoted");
        // The failover command runs against the resolved replica's headless FQDN, not "1.…".
        Assert.Contains(_connFactory.Calls,
            c => c.Host == "ag-1.ag-headless.default.svc.cluster.local" && c.Port == 1433 && c.Password == "pw");
        Assert.Equal("ag-1", ag.Status!.PrimaryReplica);
    }

    [Fact]
    public async Task ForceFailoverAnnotation_PerformsForcedFailover_AndClearsAnnotations()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.ForceFailoverToAnnotation] = "ag-2" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-2", "SECONDARY", false));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, outcome);
        Assert.Single(_agManager.ForceFailoverCalls, a => a == "ag");
        Assert.DoesNotContain(AgFailoverOrchestrator.ForceFailoverToAnnotation, ag.Metadata.Annotations);
        Assert.Single(_k8s.Updated<SqlAvailabilityGroup>());
    }

    [Fact]
    public async Task PlannedFailoverAnnotation_UnknownTarget_IsRejected()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "9" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
    }

    [Fact]
    public async Task PlannedFailover_TargetAlreadyPrimary_IsANoop()
    {
        // The planned annotation is no longer consumed before execution, so a replay
        // must not fire FORCE_FAILOVER against the current primary.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-0" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "FailoverNoop");
        Assert.DoesNotContain(AgFailoverOrchestrator.FailoverToAnnotation, ag.Metadata.Annotations);
    }

    [Fact]
    public async Task PlannedFailover_TransientFailure_KeepsTheRequestAndRetries()
    {
        // A transient error used to make the failover request vanish (the annotation was
        // consumed up front) — the user's intent silently disappeared.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _connFactory.ConnectionForHost = host => host.StartsWith("ag-1.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("target briefly unreachable")
            : new Microsoft.Data.SqlClient.SqlConnection($"Server={host}");

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Contains(AgFailoverOrchestrator.FailoverToAnnotation, ag.Metadata.Annotations);
        Assert.Equal(1, ag.Status!.PendingFailover!.Attempts);
    }

    [Fact]
    public async Task NoPrimary_ConfirmedSecondary_StaysSecondary_NotForcedToResolving()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Lost("ag-1"));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Contains(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "secondary");
        Assert.DoesNotContain(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "resolving");
    }

    [Fact]
    public async Task NoPrimary_ReplicaStillReportingPrimary_IsForcedToResolving()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-0", "PRIMARY", ready: false), Lost("ag-1"));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Contains(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-0" && c.Role == "resolving");
        Assert.Contains(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-0" && !c.Readable && !c.ReadIntentReady && c.ListenerReady == "none");
    }

    [Fact]
    public async Task PlannedAnnotation_TargetNotReady_WithoutDataLoss_IsRejected()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: false));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.DoesNotContain(AgFailoverOrchestrator.FailoverToAnnotation, ag.Metadata.Annotations);
    }

    [Fact]
    public async Task NoPrimary_BelowThreshold_DefersAction()
    {
        var ag = CreateAg(allowDataLoss: true);
        ag.Spec.Failover!.PrimaryUnreachableThreshold = 3;
        var health = Health(null, Lost("ag-1", ready: true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Equal(1, ag.Status!.PrimaryUnreachableCount);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agPodManager.ReadabilityCalls);
    }

    [Fact]
    public async Task NoPrimary_ThresholdReachedButWindowNotElapsed_DefersAction()
    {
        // Three passes can elapse in seconds (each status write re-enqueues the AG), so
        // a pass counter alone let a brief operator↔primary blip force a promotion. The wall-clock
        // window must ALSO have elapsed.
        var ag = CreateAg(allowDataLoss: true);
        ag.Spec.Failover!.PrimaryUnreachableThreshold = 3;
        SynchronizedAtLoss(ag, 1, "ag-1");
        var health = Health(null, Lost("ag-1", ready: true));

        for (var i = 0; i < 3; i++)
        {
            var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
            Assert.Equal(FailoverOutcome.NoAction, outcome);
            _time.Advance(TimeSpan.FromSeconds(1)); // three back-to-back passes in 3 seconds
        }

        Assert.Equal(3, ag.Status!.PrimaryUnreachableCount);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);

        // Once the window really has elapsed, the failover proceeds.
        _time.Advance(TimeSpan.FromSeconds(30));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";
        var final = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        Assert.Equal(FailoverOutcome.PlannedFailover, final);
    }

    [Fact]
    public async Task HealthyPass_ResetsUnreachableCounterAndWindow()
    {
        var ag = CreateAg();
        ag.Status!.PrimaryUnreachableCount = 2;
        ag.Status.PrimaryUnreachableSince = _time.GetUtcNow().UtcDateTime;
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(0, ag.Status.PrimaryUnreachableCount);
        Assert.Null(ag.Status.PrimaryUnreachableSince);
    }

    [Fact]
    public async Task SplitBrain_RefusesAnyFailover_AndDrainsWriterPath()
    {
        var ag = CreateAg(allowDataLoss: true);
        var health = new AgHealthReport(false, null,
            [Replica("ag-0", "PRIMARY", true), Replica("ag-1", "PRIMARY", true)], [], [],
            ["ag-0", "ag-1"], [], DetectedPrimaries: ["ag-0", "ag-1"]);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
    }

    [Fact]
    public async Task AutoForcedFailover_LagAboveMaxDataLoss_IsBlocked()
    {
        // An automatic promotion that accepts data loss must accept a BOUNDED amount.
        var ag = CreateAg(allowDataLoss: true);
        ag.Spec.Failover!.MaxDataLossKb = 1024;
        var replicas = new[] { Lost("ag-1") };
        var dbs = new List<AgDatabaseStateInfo>
        {
            new("app", "ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, LogSendQueueSize: 50_000, RedoQueueSize: 0),
        };
        var health = new AgHealthReport(false, null, replicas.ToList(), dbs, [], ["ag-1"], []);

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "AutoForceFailoverBlocked");
    }

    [Fact]
    public async Task AutoForcedFailover_DisarmedDuringRollingUpdate()
    {
        // Mixed-version windows are the worst moment to force-promote — an un-upgraded
        // replica cannot redo the log of an upgraded primary.
        var ag = CreateAg(allowDataLoss: true);
        ag.Status!.RollingUpdate = new AgRollingUpdateStatus { Phase = "UpdatingSecondary" };
        var health = Health(null, Lost("ag-1"));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "AutoForceFailoverBlocked");
    }

    [Fact]
    public async Task Failover_ToOlderMajorVersion_IsRejected()
    {
        // Promoting SQL Server 2019 while the databases were last primary on 2022 would
        // strand every write made since the upgrade — the older engine cannot redo the newer log.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        ag.Status!.PrimaryProductVersion = "16.0.4125.3"; // 2022
        var health = new AgHealthReport(true, "ag-0",
            [Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", true)], [], [],
            ["ag-0", "ag-1"], [], null,
            new Dictionary<string, string> { ["ag-0"] = "16.0.4125.3", ["ag-1"] = "15.0.4223.1" }); // ag-1 is 2019

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "FailoverRejected" && c.Message.Contains("15.0"));
    }

    [Fact]
    public async Task NoPrimary_NoSurvivorAheadOnEveryDatabase_IsNotProvablyLossless()
    {
        // ag-1 is ahead on "app", ag-2 on "crm": whichever is promoted loses what the other holds.
        var ag = CreateAg(allowDataLoss: false);
        SynchronizedAtLoss(ag, 1, "ag-1", "ag-2");
        var health = new AgHealthReport(false, null, [Lost("ag-1"), Lost("ag-2")],
            [LocalDb("ag-1", 120, "app"), LocalDb("ag-1", 50, "crm"), LocalDb("ag-2", 100, "app"), LocalDb("ag-2", 80, "crm")],
            [], ["ag-1", "ag-2"], []);

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
    }

    [Fact]
    public async Task DeadPrimary_SurvivorDisconnectedFromIt_ForcedFailoverStillFires()
    {
        // With a DEAD primary the report is secondary-sourced, and the
        // survivor's own connected_state describes its session to the dead primary — DISCONNECTED by
        // definition of the scenario, with a degraded sync health. The old CONNECTED gate then
        // disqualified every candidate and automatic failover never fired in the one case it exists
        // for. Liveness is proven by the probe (ReachableReplicas), not by that DMV column.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, conn: "DISCONNECTED", sync: "NOT_HEALTHY"));
        OldPrimaryUnreachable();
        _fencer.Result = FakeReplicaFencer.Confirmed;

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, outcome);
        Assert.Single(_agManager.ForceFailoverCalls, a => a == "ag");
        Assert.Equal("ag-1", ag.Status!.PrimaryReplica);
        Assert.Single(_fencer.FenceCalls, p => p == "ag-0"); // the dead primary was still proven fenced
    }

    [Fact]
    public async Task ResurrectedOldPrimary_AfterRecordedFailover_IsDemotedNotDrained()
    {
        // Fencing proves isolation at promotion time only — the StatefulSet recreates
        // the pod, which boots PRIMARY again from its own AG metadata. When the two detected
        // primaries are exactly the From/To of the operator's own last failover, the stale side is a
        // matter of record: demote it instead of draining BOTH primaries into a write outage.
        var ag = CreateAg();
        ag.Status = new SqlAvailabilityGroupStatus
        {
            PrimaryReplica = "ag-1",
            LastFailover = new AgLastFailoverStatus { From = "ag-0", To = "ag-1", Reason = "AutoForceFailover", DataLoss = true },
        };
        var replicas = new[] { Replica("ag-0", "PRIMARY", true), Replica("ag-1", "PRIMARY", true) };
        var health = new AgHealthReport(false, null, replicas.ToList(), [], [],
            ["ag-0", "ag-1"], [], DetectedPrimaries: ["ag-0", "ag-1"]);
        _agManager.RoleResolver = _ => "PRIMARY"; // ag-0 still claims the role until demoted

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Single(_agManager.DemoteCalls, a => a == "ag");
        Assert.Contains("ResurrectedPrimaryDemoted", _eventPublisher.Reasons);
        Assert.DoesNotContain("SplitBrainDetected", _eventPublisher.Reasons);
        // Only the resurrected side is de-routed; the legitimate primary keeps serving writes.
        Assert.Contains(_agPodManager.RoleLabelCalls, c => c is { Pod: "ag-0", Role: "resolving" });
        Assert.DoesNotContain(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1");
    }

    [Fact]
    public async Task ResurrectedOldPrimary_RecordedPrimaryClearedBySplitBrain_IsStillDemoted()
    {
        // A split-brain pass clears status.primaryReplica. Requiring it to equal
        // lastFailover.to made the first failed attempt the last one — the AG stayed drained for good.
        var ag = CreateAg();
        ag.Status = new SqlAvailabilityGroupStatus
        {
            PrimaryReplica = null,
            LastFailover = new AgLastFailoverStatus { From = "ag-0", To = "ag-1", Reason = "PlannedFailover" },
        };
        var replicas = new[] { Replica("ag-0", "PRIMARY", true), Replica("ag-1", "PRIMARY", true) };
        var health = new AgHealthReport(false, null, replicas.ToList(), [], [],
            ["ag-0", "ag-1"], [], DetectedPrimaries: ["ag-0", "ag-1"]);
        _agManager.RoleResolver = _ => "PRIMARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Contains("ResurrectedPrimaryDemoted", _eventPublisher.Reasons);
        // OFFLINE, then the secondary role: SET (ROLE = SECONDARY) alone fails with 41104 on an online primary.
        Assert.Single(_agManager.DemoteCalls);
        Assert.Single(_agManager.SetSecondaryHosts, h => h.StartsWith("ag-0.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlannedFailover_ReachablePrimaryRefusesDemotion_IsAbortedNeverFenced()
    {
        // The demotion of a healthy, reachable primary failed (41104) and was taken for
        // unreachability: its pod was deleted in the middle of a planned failover.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";
        _agManager.DemoteFails = c => c.DataSource.StartsWith("ag-0.", StringComparison.Ordinal);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Empty(_fencer.FenceCalls);
        Assert.Null(ag.Status!.Fencing);
        Assert.Empty(_agManager.PromotionHosts);
        Assert.Contains(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-0" && c.ListenerReady == "writer");
    }

    [Fact]
    public async Task PlannedFailover_OldPrimaryTakesTheSecondaryRole_AfterThePromotion()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.PromotionHosts, h => h.StartsWith("ag-1.", StringComparison.Ordinal));
        Assert.Single(_agManager.SetSecondaryHosts, h => h.StartsWith("ag-0.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlannedFailover_BystanderSecondary_ReTakesTheSecondaryRole()
    {
        // A secondary that was neither the old primary nor the target stayed
        // SUSPEND_FROM_PARTNER on the new primary until it took the secondary role again.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true),
            Replica("ag-2", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Contains(_agManager.SetSecondaryHosts, h => h.StartsWith("ag-2.", StringComparison.Ordinal));
        Assert.Contains(_agManager.SetSecondaryHosts, h => h.StartsWith("ag-0.", StringComparison.Ordinal));
        Assert.DoesNotContain(_agManager.SetSecondaryHosts, h => h.StartsWith("ag-1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitBrain_WithoutMatchingFailoverRecord_StaysDrainedAndManual()
    {
        // No recorded failover designates a survivor → the conservative drain must remain.
        var ag = CreateAg();
        var replicas = new[] { Replica("ag-0", "PRIMARY", true), Replica("ag-1", "PRIMARY", true) };
        var health = new AgHealthReport(false, null, replicas.ToList(), [], [],
            ["ag-0", "ag-1"], [], DetectedPrimaries: ["ag-0", "ag-1"]);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.DemoteCalls);
        Assert.Contains("SplitBrainDetected", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task PrimaryRestartInPlaceDuringRollingUpdate_SuspendsAutoFailover()
    {
        // With failoverBeforePrimaryUpdate=false the rolling update deletes the primary
        // pod on purpose. A crash recovery outlives the unreachable window, so the auto-failover used
        // to fence the pod that was merely booting and manufacture the resurrection split-brain.
        var ag = CreateAg();
        ag.Status!.RollingUpdate = new AgRollingUpdateStatus { Phase = "UpdatingPrimary", CurrentPod = "ag-0" };
        var health = Health(null, Lost("ag-1", ready: true));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.NoAction, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Empty(_fencer.FenceCalls);
        Assert.Contains("AutoFailoverSuspendedDuringPrimaryUpdate", _eventPublisher.Reasons);
    }
}
