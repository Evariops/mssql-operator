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
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly OperatorMetrics _metrics = new();
    private readonly AgFailoverOrchestrator _sut;

    public AgFailoverOrchestratorTests()
    {
        // Fake defaults: connection factory returns a throwaway SqlConnection, update echoes the
        // entity, fencing is confirmed, local role confirmation returns PRIMARY.
        _sut = new AgFailoverOrchestrator(
            NullLogger<AgFailoverOrchestrator>.Instance, _k8s, _connFactory,
            _agManager, _agPodManager, _eventPublisher, _metrics);
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
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls, a => a == "ag");
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Equal("ag-1", ag.Status!.PrimaryReplica);
        Assert.False(ag.Status.LastFailover!.DataLoss);
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossDisallowed_AwaitsManualIntervention()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossAllowed_ForcesFailoverAndFencesOldPrimary()
    {
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, outcome);
        Assert.Single(_agManager.ForceFailoverCalls, a => a == "ag");
        // Old primary ag-0 must be fenced (pod deleted) before the forced promotion.
        Assert.Single(_agPodManager.FenceByDeleteCalls, p => p == "ag-0");
        Assert.True(ag.Status!.LastFailover!.DataLoss);
    }

    [Fact]
    public async Task ForceFailoverAnnotation_PerformsForcedFailover_AndClearsAnnotations()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.ForceFailoverToAnnotation] = "ag-2" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-2", "SECONDARY", false));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.ForcedFailover, outcome);
        Assert.Single(_agManager.ForceFailoverCalls, a => a == "ag");
        Assert.DoesNotContain(AgFailoverOrchestrator.ForceFailoverToAnnotation, ag.Metadata.Annotations);
        Assert.Single(_k8s.Updated<SqlAvailabilityGroup>());
    }

    [Fact]
    public async Task PlannedFailoverAnnotation_AcceptsOrdinal_TargetsThatReplicaByFqdn()
    {
        // The documented contract is an ordinal (failover-to: "1" => ag-1). Regression for the bug
        // where "1" was passed straight through as the replica name, producing the connection host
        // "1.ag-headless.default.svc.cluster.local" (NXDOMAIN) and a failed failover.
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.PlannedFailover, outcome);
        Assert.Single(_agManager.FailoverCalls, a => a == "ag");
        // The failover command must run against the resolved replica's headless FQDN, not "1.…".
        Assert.Single(_connFactory.Calls,
            c => c.Host == "ag-1.ag-headless.default.svc.cluster.local" && c.Port == 1433 && c.Password == "pw" && c.Database == "master");
        Assert.Equal("ag-1", ag.Status!.PrimaryReplica);
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
    public async Task NoPrimary_HealthySecondary_KeptReadable_NotForcedToResolving()
    {
        // No primary, ag-1 is a connected/healthy secondary but not failover-ready → AwaitingManual.
        // The write path is drained but the secondary must keep serving reads.
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Single(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "secondary");
        Assert.DoesNotContain(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-1" && c.Role == "resolving");
        Assert.Single(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-1" && c.Readable && c.ReadIntentReady && c.ListenerReady == "reader");
    }

    [Fact]
    public async Task NoPrimary_ReplicaStillReportingPrimary_IsForcedToResolving()
    {
        // A reachable pod still claiming PRIMARY (stale/split-brain) must be fully de-routed.
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-0", "PRIMARY", ready: false), Replica("ag-1", "SECONDARY", ready: false));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Single(_agPodManager.RoleLabelCalls, c => c.Pod == "ag-0" && c.Role == "resolving");
        Assert.Single(_agPodManager.ReadabilityCalls, c => c.Pod == "ag-0" && !c.Readable && !c.ReadIntentReady && c.ListenerReady == "none");
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
    public async Task ForcedFailover_FencingNotConfirmed_AbortsWithoutDataLossPromotion()
    {
        // No FORCE_FAILOVER_ALLOW_DATA_LOSS while the old primary may still write.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));
        _agPodManager.FenceByDeleteResult = false;

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.Failed, outcome);
        Assert.Empty(_agManager.ForceFailoverCalls);
        Assert.Single(_eventPublisher.Calls, c => c.Reason == "FencingFailed" && c.Type == "Warning");
    }

    [Fact]
    public async Task NoPrimary_BelowThreshold_DefersAction()
    {
        // A single missed pass must not drain the writer label or promote anything.
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
    public async Task HealthyPass_ResetsUnreachableCounter()
    {
        var ag = CreateAg();
        ag.Status!.PrimaryUnreachableCount = 2;
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(0, ag.Status.PrimaryUnreachableCount);
    }

    [Fact]
    public async Task SplitBrain_RefusesAnyFailover_AndDrainsWriterPath()
    {
        // Two primaries — no promotion, no arbitrary pick.
        var ag = CreateAg(allowDataLoss: true);
        var health = new AgHealthReport(false, null,
            [Replica("ag-0", "PRIMARY", true), Replica("ag-1", "PRIMARY", true)], [], [],
            ["ag-0", "ag-1"], [], DetectedPrimaries: ["ag-0", "ag-1"]);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        Assert.Equal(FailoverOutcome.AwaitingManualIntervention, outcome);
        Assert.Empty(_agManager.FailoverCalls);
        Assert.Empty(_agManager.ForceFailoverCalls);
    }
}
