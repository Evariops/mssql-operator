using FluentAssertions;
using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class AgFailoverOrchestratorTests
{
    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlAgManager> _agManager = new();
    private readonly Mock<IAgPodManager> _agPodManager = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly OperatorMetrics _metrics = new();
    private readonly AgFailoverOrchestrator _sut;

    public AgFailoverOrchestratorTests()
    {
        _connFactory
            .Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SqlConnection());
        _k8s.Setup(x => x.UpdateAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);
        // Fencing confirmed by default; individual tests override to simulate failure.
        _agPodManager.Setup(x => x.FenceByDeletePodAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new AgFailoverOrchestrator(
            Mock.Of<ILogger<AgFailoverOrchestrator>>(), _k8s.Object, _connFactory.Object,
            _agManager.Object, _agPodManager.Object, _eventPublisher.Object, _metrics);
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
                Failover = new AgFailoverSpec { OperatorManaged = true, AllowAutomaticDataLossFailover = allowDataLoss }
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

        outcome.Should().Be(FailoverOutcome.NoAction);
        _agManager.Verify(x => x.FailoverAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoPrimary_WithSyncReadySecondary_PerformsPlannedFailover()
    {
        var ag = CreateAg();
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: true));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.PlannedFailover);
        _agManager.Verify(x => x.FailoverAsync(It.IsAny<SqlConnection>(), "ag", It.IsAny<CancellationToken>()), Times.Once);
        _agManager.Verify(x => x.ForceFailoverWithDataLossAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ag.Status!.PrimaryReplica.Should().Be("ag-1");
        ag.Status.LastFailover!.DataLoss.Should().BeFalse();
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossDisallowed_AwaitsManualIntervention()
    {
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.AwaitingManualIntervention);
        _agManager.Verify(x => x.FailoverAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _agManager.Verify(x => x.ForceFailoverWithDataLossAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoPrimary_NoSyncReady_DataLossAllowed_ForcesFailoverAndFencesOldPrimary()
    {
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.ForcedFailover);
        _agManager.Verify(x => x.ForceFailoverWithDataLossAsync(It.IsAny<SqlConnection>(), "ag", It.IsAny<CancellationToken>()), Times.Once);
        // Old primary ag-0 must be fenced (pod deleted) before the forced promotion.
        _agPodManager.Verify(x => x.FenceByDeletePodAsync("ag-0", "default", It.IsAny<CancellationToken>()), Times.Once);
        ag.Status!.LastFailover!.DataLoss.Should().BeTrue();
    }

    [Fact]
    public async Task ForceFailoverAnnotation_PerformsForcedFailover_AndClearsAnnotations()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.ForceFailoverToAnnotation] = "ag-2" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-2", "SECONDARY", false));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.ForcedFailover);
        _agManager.Verify(x => x.ForceFailoverWithDataLossAsync(It.IsAny<SqlConnection>(), "ag", It.IsAny<CancellationToken>()), Times.Once);
        ag.Metadata.Annotations.Should().NotContainKey(AgFailoverOrchestrator.ForceFailoverToAnnotation);
        _k8s.Verify(x => x.UpdateAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoPrimary_HealthySecondary_KeptReadable_NotForcedToResolving()
    {
        // No primary, ag-1 is a connected/healthy secondary but not failover-ready → AwaitingManual.
        // The write path is drained but the secondary must keep serving reads.
        var ag = CreateAg(allowDataLoss: false);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "HEALTHY"));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.AwaitingManualIntervention);
        _agPodManager.Verify(x => x.SetPodRoleLabelAsync("ag-1", "default", "ag", "secondary", It.IsAny<CancellationToken>()), Times.Once);
        _agPodManager.Verify(x => x.SetPodRoleLabelAsync("ag-1", "default", "ag", "resolving", It.IsAny<CancellationToken>()), Times.Never);
        _agPodManager.Verify(x => x.SetReadabilityLabelsAsync("ag-1", "default", true, true, "reader", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoPrimary_ReplicaStillReportingPrimary_IsForcedToResolving()
    {
        // A reachable pod still claiming PRIMARY (stale/split-brain) must be fully de-routed.
        var ag = CreateAg(allowDataLoss: false);
        // No agreed primary, yet a reachable pod still claims PRIMARY (stale/split-brain).
        var health = Health(null, Replica("ag-0", "PRIMARY", ready: false), Replica("ag-1", "SECONDARY", ready: false));

        await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        _agPodManager.Verify(x => x.SetPodRoleLabelAsync("ag-0", "default", "ag", "resolving", It.IsAny<CancellationToken>()), Times.Once);
        _agPodManager.Verify(x => x.SetReadabilityLabelsAsync("ag-0", "default", false, false, "none", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlannedAnnotation_TargetNotReady_WithoutDataLoss_IsRejected()
    {
        var annotations = new Dictionary<string, string> { [AgFailoverOrchestrator.FailoverToAnnotation] = "ag-1" };
        var ag = CreateAg(annotations: annotations);
        var health = Health("ag-0", Replica("ag-0", "PRIMARY", true), Replica("ag-1", "SECONDARY", ready: false));

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.AwaitingManualIntervention);
        _agManager.Verify(x => x.FailoverAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        ag.Metadata.Annotations.Should().NotContainKey(AgFailoverOrchestrator.FailoverToAnnotation);
    }

    [Fact]
    public async Task ForcedFailover_FencingNotConfirmed_AbortsWithoutDataLossPromotion()
    {
        // No FORCE_FAILOVER_ALLOW_DATA_LOSS while the old primary may still write.
        var ag = CreateAg(allowDataLoss: true);
        var health = Health(null, Replica("ag-1", "SECONDARY", ready: false, sync: "NOT_HEALTHY"));
        _agPodManager.Setup(x => x.FenceByDeletePodAsync("ag-0", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var outcome = await _sut.ReconcileFailoverAsync(ag, "pw", health, CancellationToken.None);

        outcome.Should().Be(FailoverOutcome.Failed);
        _agManager.Verify(x => x.ForceFailoverWithDataLossAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisher.Verify(x => x.PublishAsync(It.IsAny<SqlAvailabilityGroup>(), "FencingFailed",
            It.IsAny<string>(), "Warning", It.IsAny<CancellationToken>()), Times.Once);
    }
}
