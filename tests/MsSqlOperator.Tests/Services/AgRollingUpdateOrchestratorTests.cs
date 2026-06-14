using k8s.Models;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class AgRollingUpdateOrchestratorTests
{
    private const string Target = "rev-2";
    private const string Old = "rev-1";

    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly Mock<IAgPodManager> _agPodManager = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly OperatorMetrics _metrics = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AgRollingUpdateOrchestrator _sut;

    private readonly List<V1Pod> _pods = [];

    public AgRollingUpdateOrchestratorTests()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _k8s.Setup(x => x.GetAsync<V1StatefulSet>("ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1StatefulSet
            {
                Metadata = new V1ObjectMeta { Name = "ag" },
                Spec = new V1StatefulSetSpec { Replicas = 3 },
                Status = new V1StatefulSetStatus { UpdateRevision = Target }
            });
        _agPodManager.Setup(x => x.GetAgPodsAsync("ag", "default", "ag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pods);
        _k8s.Setup(x => x.GetAsync<V1Pod>(It.IsAny<string>(), "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string n, string ns, CancellationToken _) => _pods.FirstOrDefault(p => p.Metadata.Name == n));
        _k8s.Setup(x => x.UpdateAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);

        _sut = new AgRollingUpdateOrchestrator(
            Mock.Of<ILogger<AgRollingUpdateOrchestrator>>(), _k8s.Object, _agPodManager.Object,
            _eventPublisher.Object, _metrics, _time);
    }

    private void Pod(string name, string revision, bool ready = true) => _pods.Add(new V1Pod
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default", Labels = new Dictionary<string, string> { ["controller-revision-hash"] = revision } },
        Status = new V1PodStatus { ContainerStatuses = [new V1ContainerStatus { Ready = ready, Name = "mssql", Image = "x", ImageID = "x", RestartCount = 0 }] }
    });

    private static SqlAvailabilityGroup Ag(AgRollingUpdateStatus? ru = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default", Uid = "uid" },
        Spec = new SqlAvailabilityGroupSpec
        {
            Replicas = new AgReplicaTopologySpec { Count = 3 },
            UpdateStrategy = new UpdateStrategySpec { RollingUpdate = new RollingUpdateSpec { PauseBetweenPodsSeconds = 0 } }
        },
        Status = new SqlAvailabilityGroupStatus { RollingUpdate = ru }
    };

    private static AgReplicaStateInfo R(string name, string role, string mode, bool ready = true, string conn = "CONNECTED", string sync = "HEALTHY")
        => new(name, role, sync, conn, "ONLINE", ready, "ALL", mode);

    private static AgHealthReport Health(params AgReplicaStateInfo[] replicas)
    {
        var primary = replicas.FirstOrDefault(r => r.Role == "PRIMARY")?.Name;
        return new AgHealthReport(primary != null, primary, replicas.ToList(), [], [], replicas.Select(r => r.Name).ToList(), []);
    }

    [Fact]
    public async Task AllPodsAtTargetRevision_ReturnsIdle()
    {
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Target);
        var outcome = await _sut.ReconcileAsync(Ag(), Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Idle, outcome);
        _k8s.Verify(x => x.DeleteAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AllUpdated_AfterBeingInProgress_EmitsCompletedAndGoesIdle()
    {
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Target);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary" });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Idle, outcome);
        Assert.Equal("Idle", ag.Status!.RollingUpdate!.Phase);
        _eventPublisher.Verify(x => x.PublishAsync(It.IsAny<SqlAvailabilityGroup>(), "RollingUpdateCompleted", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StaleSecondaries_DeletesAsyncBeforeSync_HighestOrdinalFirst()
    {
        // All stale; ag-2 async, ag-1 sync secondary, ag-0 primary sync.
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag();

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("ag-2", ag.Status!.RollingUpdate!.CurrentPod); // async secondary updated first
        _k8s.Verify(x => x.DeleteAsync(It.Is<V1Pod>(p => p.Metadata.Name == "ag-2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnhealthyCluster_DoesNotDeleteAnyPod()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var outcome = await _sut.ReconcileAsync(Ag(), Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", conn: "DISCONNECTED")), // degraded
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        _k8s.Verify(x => x.DeleteAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitingOnCurrentPod_StillStale_DoesNotDeleteAnother()
    {
        // ag-2 was deleted last cycle and is still rebuilding (still old revision).
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary", CurrentPod = "ag-2", CurrentStepStartedAt = _time.GetUtcNow().UtcDateTime });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        _k8s.Verify(x => x.DeleteAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnlyPrimaryStale_SetsPlannedFailoverAnnotationToUpdatedSecondary()
    {
        Pod("ag-0", Old);     // primary still old
        Pod("ag-1", Target);  // updated, failover-ready
        Pod("ag-2", Target);
        var ag = Ag();

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", ready: true),
            R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("FailingOverForUpdate", ag.Status!.RollingUpdate!.Phase);
        Assert.Equal("ag-1", ag.Metadata.Annotations![AgFailoverOrchestrator.FailoverToAnnotation]);
        _k8s.Verify(x => x.DeleteAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnlyPrimaryStale_NoUpdatedFailoverReadyTarget_Stalls()
    {
        Pod("ag-0", Old); Pod("ag-1", Target); Pod("ag-2", Target);
        var ag = Ag();

        // Secondaries updated but NOT failover-ready (ready:false).
        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", ready: false),
            R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT", ready: false)),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
    }

    [Fact]
    public async Task FailingOverForUpdate_WithinTimeout_WaitsWithoutRepostingAnnotation()
    {
        Pod("ag-0", Old); Pod("ag-1", Target); Pod("ag-2", Target); // only primary stale
        var ag = Ag(new AgRollingUpdateStatus { Phase = "FailingOverForUpdate", CurrentStepStartedAt = _time.GetUtcNow().UtcDateTime });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("WaitingFailover", ag.Status!.RollingUpdate!.Step);
        // Set-once: the failover annotation is NOT re-posted while waiting.
        _k8s.Verify(x => x.UpdateAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailingOverForUpdate_ExceedsTimeout_MarksFailed()
    {
        Pod("ag-0", Old); Pod("ag-1", Target); Pod("ag-2", Target);
        var longAgo = _time.GetUtcNow().UtcDateTime.AddSeconds(-1000);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "FailingOverForUpdate", CurrentStepStartedAt = longAgo });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
    }

    [Fact]
    public async Task FailedRollout_NoRetryAnnotation_StaysStalledAndDoesNothing()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "Failed" });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
        _k8s.Verify(x => x.DeleteAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedRollout_WithRetryAnnotation_ResumesAndUpdates()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "Failed" });
        ag.Metadata.Annotations = new Dictionary<string, string> { [AgRollingUpdateOrchestrator.RetryAnnotation] = "true" };

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.NotEqual("Failed", ag.Status!.RollingUpdate!.Phase);
        Assert.DoesNotContain(AgRollingUpdateOrchestrator.RetryAnnotation, ag.Metadata.Annotations);
        _k8s.Verify(x => x.DeleteAsync(It.Is<V1Pod>(p => p.Metadata.Name == "ag-2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrentPod_ExceedsTimeout_Stalls()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old, ready: false); // ag-2 stuck not ready
        var startedLongAgo = _time.GetUtcNow().UtcDateTime.AddSeconds(-1000);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary", CurrentPod = "ag-2", CurrentStepStartedAt = startedLongAgo });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
    }
}
