using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class AgRollingUpdateOrchestratorTests
{
    private const string Target = "rev-2";
    private const string Old = "rev-1";

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeAgPodManager _agPodManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly OperatorMetrics _metrics = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeFullTextManager _fullText = new();
    private readonly AgRollingUpdateOrchestrator _sut;

    private readonly List<V1Pod> _pods = [];

    public AgRollingUpdateOrchestratorTests()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _k8s.SetGet("ag", "default", new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = "ag" },
            Spec = new V1StatefulSetSpec { Replicas = 3 },
            Status = new V1StatefulSetStatus { UpdateRevision = Target }
        });
        _agPodManager.AgPods = _pods;

        _sut = new AgRollingUpdateOrchestrator(
            NullLogger<AgRollingUpdateOrchestrator>.Instance, _k8s, _agPodManager,
            _eventPublisher, _connFactory, _agManager, _fullText, _metrics, _time);
    }

    private void Pod(string name, string revision, bool ready = true)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default", Labels = new Dictionary<string, string> { ["controller-revision-hash"] = revision } },
            Status = new V1PodStatus { ContainerStatuses = [new V1ContainerStatus { Ready = ready, Name = "mssql", Image = "x", ImageID = "x", RestartCount = 0 }] }
        };
        _pods.Add(pod);
        _k8s.SetGet(name, "default", pod); // GetAsync<V1Pod>(name) resolves to this pod
    }

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
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Idle, outcome);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Fact]
    public async Task AllUpdated_AfterBeingInProgress_EmitsCompletedAndGoesIdle()
    {
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Target);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary" });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Idle, outcome);
        Assert.Equal("Idle", ag.Status!.RollingUpdate!.Phase);
        Assert.Single(_eventPublisher.Calls, c => c.Reason == "RollingUpdateCompleted");
    }

    [Fact]
    public async Task StaleSecondaries_DeletesAsyncBeforeSync_HighestOrdinalFirst()
    {
        // All stale; ag-2 async, ag-1 sync secondary, ag-0 primary sync.
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag();

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("ag-2", ag.Status!.RollingUpdate!.CurrentPod); // async secondary updated first
        Assert.Single(_k8s.Deleted<V1Pod>(), p => p.Metadata.Name == "ag-2");
    }

    [Fact]
    public async Task UnhealthyCluster_DoesNotDeleteAnyPod()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var outcome = await _sut.ReconcileAsync(Ag(), Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", conn: "DISCONNECTED")), // degraded
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Fact]
    public async Task WaitingOnCurrentPod_StillStale_DoesNotDeleteAnother()
    {
        // ag-2 was deleted last cycle and is still rebuilding (still old revision).
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary", CurrentPod = "ag-2", CurrentStepStartedAt = _time.GetUtcNow().UtcDateTime });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Fact]
    public async Task OnlyPrimaryStale_RequestsAPlannedFailoverToTheUpdatedSecondary()
    {
        Pod("ag-0", Old);     // primary still old
        Pod("ag-1", Target);  // updated, failover-ready
        Pod("ag-2", Target);
        var ag = Ag();

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", ready: true),
            R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("FailingOverForUpdate", ag.Status!.RollingUpdate!.Phase);
        // Requested through the status — the user's annotations are left alone.
        Assert.Equal("ag-1", ag.Status.RollingUpdate.FailoverTarget);
        Assert.Null(ag.Metadata.Annotations);
        Assert.Empty(_k8s.UpdateCalls);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyPrimaryStale_InPlaceUpdate_PhaseIsPersistedBeforeThePrimaryIsDeleted(bool patchFails)
    {
        // UpdatingPrimary is written before the primary pod goes away; when it cannot be
        // written, the pod is not deleted and nothing is left half-recorded.
        Pod("ag-0", Old);
        Pod("ag-1", Target);
        Pod("ag-2", Target);
        var ag = Ag();
        ag.Spec.UpdateStrategy!.RollingUpdate!.FailoverBeforePrimaryUpdate = false;
        if (patchFails)
            _k8s.PatchStatusFailure = new InvalidOperationException("409 Conflict");

        await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"),
            R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        if (patchFails)
        {
            Assert.Empty(_k8s.Deleted<V1Pod>());
            Assert.NotEqual("UpdatingPrimary", ag.Status!.RollingUpdate?.Phase);
        }
        else
        {
            var patch = Assert.Single(_k8s.PatchStatusCalls);
            Assert.True(patch.Patch.ContainsKey("rollingUpdate"));
            Assert.Equal("UpdatingPrimary", ag.Status!.RollingUpdate!.Phase);
            Assert.NotEmpty(_k8s.Deleted<V1Pod>());
        }
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
            "pw", CancellationToken.None);

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
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Equal("WaitingFailover", ag.Status!.RollingUpdate!.Step);
        // Set-once: the failover annotation is NOT re-posted while waiting.
        Assert.Empty(_k8s.Updated<SqlAvailabilityGroup>());
    }

    [Fact]
    public async Task FailingOverForUpdate_ExceedsTimeout_MarksFailed()
    {
        Pod("ag-0", Old); Pod("ag-1", Target); Pod("ag-2", Target);
        var longAgo = _time.GetUtcNow().UtcDateTime.AddSeconds(-1000);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "FailingOverForUpdate", CurrentStepStartedAt = longAgo });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

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
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Fact]
    public async Task FailedRollout_WithRetryAnnotation_ResumesAndUpdates()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "Failed" });
        ag.Metadata.Annotations = new Dictionary<string, string> { [AgRollingUpdateOrchestrator.RetryAnnotation] = "true" };

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.NotEqual("Failed", ag.Status!.RollingUpdate!.Phase);
        Assert.DoesNotContain(AgRollingUpdateOrchestrator.RetryAnnotation, ag.Metadata.Annotations);
        Assert.Single(_k8s.Deleted<V1Pod>(), p => p.Metadata.Name == "ag-2");
    }

    [Fact]
    public async Task CurrentPod_ExceedsTimeout_Stalls()
    {
        Pod("ag-0", Old); Pod("ag-1", Old); Pod("ag-2", Old, ready: false); // ag-2 stuck not ready
        var startedLongAgo = _time.GetUtcNow().UtcDateTime.AddSeconds(-1000);
        var ag = Ag(new AgRollingUpdateStatus { Phase = "UpdatingSecondary", CurrentPod = "ag-2", CurrentStepStartedAt = startedLongAgo });

        var outcome = await _sut.ReconcileAsync(ag, Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT")),
            "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, outcome);
        Assert.Equal("Failed", ag.Status!.RollingUpdate!.Phase);
    }

    [Fact]
    public async Task FullTextPopulation_BeyondTheStallTimeout_FailsTheRollout()
    {
        // The full-text gate claimed to be bounded by the stall timeout but (a) reset
        // BlockedSince to null right before re-arming it on every healthy pass and (b) never checked
        // the timeout in its branch — a population that never ended blocked the rollout forever.
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Old);
        var ag = Ag();
        ag.Spec.UpdateStrategy!.RollingUpdate!.HealthCheckTimeoutSeconds = 60; // stall timeout = max(600, 600)
        _fullText.Status = FakeFullTextManager.Populating();
        var health = Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT"));

        var first = await _sut.ReconcileAsync(ag, health, "pw", CancellationToken.None);
        Assert.Equal(RollingUpdateOutcome.InProgress, first);
        Assert.Equal("WaitingFullText", ag.Status!.RollingUpdate!.Step);
        Assert.Empty(_k8s.Deleted<V1Pod>()); // the stale pod was NOT taken down mid-population

        _time.Advance(TimeSpan.FromSeconds(601));
        var second = await _sut.ReconcileAsync(ag, health, "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.Stalled, second);
        Assert.Equal("Failed", ag.Status.RollingUpdate.Phase);
        Assert.Contains("RollingUpdateStalled", _eventPublisher.Reasons);
        Assert.Empty(_k8s.Deleted<V1Pod>());
    }

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT", true)]
    [InlineData("ASYNCHRONOUS_COMMIT", false)] // never waited for by a commit
    public async Task UpdatingASyncSecondary_SuspendsItBeforeDeletingItsPod(string mode, bool suspended)
    {
        // Its pod went away while the primary still waited for it on every commit —
        // writes froze for the SESSION_TIMEOUT, twice per rolling update.
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Old);
        var health = Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", mode));
        health = health with
        {
            DatabaseStates =
            [
                new AgDatabaseStateInfo("ShopDB", "ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
                new AgDatabaseStateInfo("ShopDB", "ag-2", "SECONDARY", "SYNCHRONIZED", true, 0, 0),
            ],
        };

        await _sut.ReconcileAsync(Ag(), health, "pw", CancellationToken.None);

        Assert.Single(_k8s.Deleted<V1Pod>(), p => p.Metadata.Name == "ag-2");
        Assert.Equal(suspended, _agManager.SuspendDataMovementCalls.Contains("ShopDB"));
        Assert.Equal(suspended, _connFactory.ProbeCalls.Any(c => c.Host.StartsWith("ag-2.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UpdatingTheLastSyncSecondary_NeverLowersRequiredSyncBelowTheFloor()
    {
        // The pre-removal lowering ignored spec.replicas.requiredSyncFloor —
        // during the update of the last synchronous secondary, commits were acked with ZERO
        // synchronous replicas, the exact durability window promises to keep closed.
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Old);
        var ag = Ag();
        ag.Spec.Replicas.RequiredSynchronizedSecondariesToCommit = 1;
        ag.Spec.Replicas.RequiredSyncFloor = 1;
        _agManager.RequiredSyncSecondaries = 1; // current live value
        var health = Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "ASYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "SYNCHRONOUS_COMMIT"));

        var outcome = await _sut.ReconcileAsync(ag, health, "pw", CancellationToken.None);

        Assert.Equal(RollingUpdateOutcome.InProgress, outcome);
        Assert.Single(_k8s.Deleted<V1Pod>(), p => p.Metadata.Name == "ag-2");
        // The floor holds: REQUIRED_SYNC stays at 1 (commits may block — the configured trade-off).
        Assert.DoesNotContain(_agManager.SetRequiredSyncCalls, c => c.Count == 0);
        Assert.Contains("DurabilityFloorHoldingWrites", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task UpdatingTheLastSyncSecondary_OfAnAgThatTookTheQuorumDefault_HoldsTheQuorumFloor()
    {
        // The rollout read spec.replicas.durability only, so an AG whose Quorum comes from its
        // control record was updated under the Legacy floor of 0.
        Pod("ag-0", Target); Pod("ag-1", Target); Pod("ag-2", Old);
        var ag = Ag();
        ag.Metadata.Uid = "uid-rollout-default-quorum";
        ag.Spec.Replicas.RequiredSynchronizedSecondariesToCommit = 1;
        MsSqlOperator.Services.Control.DurabilityProfile.Remember(ag, "Quorum");
        _agManager.RequiredSyncSecondaries = 1;
        var health = Health(
            R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "ASYNCHRONOUS_COMMIT"), R("ag-2", "SECONDARY", "SYNCHRONOUS_COMMIT"));

        await _sut.ReconcileAsync(ag, health, "pw", CancellationToken.None);

        Assert.DoesNotContain(_agManager.SetRequiredSyncCalls, c => c.Count == 0);
        Assert.Contains("DurabilityFloorHoldingWrites", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task UpdatingTheSecondaryOfAStandardAg_NeverTouchesRequiredSync()
    {
        // A basic availability group refuses REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT.
        Pod("ag-0", Target); Pod("ag-1", Old);
        var ag = Ag();
        ag.Spec.Edition = "Standard";
        ag.Spec.Replicas.Count = 2;
        _k8s.SetGet("ag", "default", new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default" },
            Spec = new V1StatefulSetSpec { Replicas = 2 },
            Status = new V1StatefulSetStatus { UpdateRevision = Target },
        });
        _agManager.RequiredSyncSecondaries = 0;
        var health = Health(R("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"), R("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"));

        await _sut.ReconcileAsync(ag, health, "pw", CancellationToken.None);

        Assert.Single(_k8s.Deleted<V1Pod>(), p => p.Metadata.Name == "ag-1");
        Assert.Empty(_agManager.SetRequiredSyncCalls);
    }
}
