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
    public async Task NoPrimary_WithSyncReadySecondary_PerformsPlannedFailover()
    {
        var ag = CreateAg();
        // No primary answers the health pass, but the old primary is a live pod the operator can
        // still reach for demotion (the operator↔primary link is what broke).
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None); // pass 1 arms the window
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls, a => a == "ag");
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Equal("ag-1", ag.Status!.PrimaryReplica);
        Assert.False(ag.Status.LastFailover!.DataLoss);
        // The reachable old primary is DEMOTED — never left as a second primary.
        Assert.Single(_agManager.DemoteCalls, a => a == "ag");
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossDisallowed_AwaitsManualIntervention()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));
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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));
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
    public async Task NoPrimary_HealthySecondary_KeptReadable_NotForcedToResolving()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "HEALTHY"));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Contains(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "secondary");
        Assert.DoesNotContain(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "resolving");
        Assert.Contains(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-1" && c.Readable && c.ReadIntentReady && c.ListenerReady == "reader");
    }

    [Fact]
    public async Task NoPrimary_ReplicaStillReportingPrimary_IsForcedToResolving()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-0", "PRIMARY", ready: false), Replica("ag-1", "SECONDARY", ready: false));

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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

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
        var replicas = new[] { Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY") };
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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

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
    public async Task SeedingDatabase_DoesNotDisarmLosslessFailover()
    {
        // is_failover_ready is aggregated with MIN over every database, so a single
        // database being seeded (hours) marked the whole replica unusable and disarmed lossless
        // failover for the entire AG. A never-synchronized copy costs nothing to lose: it is re-seeded.
        var ag = CreateAg();
        var replicas = new[]
        {
            // Replica-level flag is false (MIN over databases) and health is PARTIALLY_HEALTHY.
            Replica("ag-1", "SECONDARY", ready: false, sync: "PARTIALLY_HEALTHY"),
        };
        var dbs = new List<AgDatabaseStateInfo>
        {
            new("app", "ag-1", "SECONDARY", "SYNCHRONIZED", IsFailoverReady: true, 0, 0),
            new("new-db", "ag-1", "SECONDARY", "INITIALIZING", IsFailoverReady: false, 0, 0),
        };
        var health = new AgHealthReport(false, null, replicas.ToList(), dbs, [], ["ag-1"], []);
        _agManager.RoleResolver = c => c.DataSource.StartsWith("ag-0", StringComparison.Ordinal) ? "PRIMARY" : "SECONDARY";

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);
        ElapseUnreachableWindow();
        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls);
    }

    [Fact]
    public async Task StaleDatabase_DoesDisarmLosslessFailover()
    {
        // The mirror image of the test above: a database that WAS synchronized and is now behind is a
        // real data-loss risk and must still block the lossless path.
        var ag = CreateAg();
        var replicas = new[] { Replica("ag-1", "SECONDARY", ready: false, sync: "PARTIALLY_HEALTHY") };
        var dbs = new List<AgDatabaseStateInfo>
        {
            new("app", "ag-1", "SECONDARY", "NOT SYNCHRONIZING", IsFailoverReady: false, 5_000, 0),
        };
        var health = new AgHealthReport(false, null, replicas.ToList(), dbs, [], ["ag-1"], []);

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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

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
