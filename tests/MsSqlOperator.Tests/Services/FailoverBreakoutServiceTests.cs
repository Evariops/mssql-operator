using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class FailoverBreakoutServiceTests
{
    private const string InstanceSelector = "mssql-operator.evariops.com/instance";
    private const string AgSelector = "mssql-operator.evariops.com/ag";

    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly FakeFencingDriver _fencingDriver = new();
    private readonly FakeBlacklistManager _blacklistManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly BreakoutMetrics _metrics = new();
    private readonly BreakoutOptions _options;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FailoverBreakoutService _service;

    public FailoverBreakoutServiceTests()
    {
        _options = new BreakoutOptions
        {
            Enabled = true,
            ControlLoopPeriodSeconds = 5,
            TerminatingThresholdSeconds = 30,
            NodeReadyGracePeriodSeconds = 60,
            PodSelector = InstanceSelector,
            FencingDriver = "volumeattachment",
            MaxConcurrentFencing = 3,
            BlacklistConfigMapName = "mssql-failover-blacklist",
            BlacklistConfigMapNamespace = "default",
            // Relocate as soon as the node is lost (the delay has its own tests), on a trusted driver.
            RelocationDelaySeconds = 0,
            TrustedFencingCsiDrivers = ["csi.driver"],
        };
        _k8sClient.SetGet("pv-data-0", null, new V1PersistentVolume
        {
            Metadata = new V1ObjectMeta { Name = "pv-data-0" },
            Spec = new V1PersistentVolumeSpec { Csi = new V1CSIPersistentVolumeSource { Driver = "csi.driver", VolumeHandle = "h" } },
        });

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 10, 15, 0, 0, TimeSpan.Zero));

        _service = new FailoverBreakoutService(
            NullLogger<FailoverBreakoutService>.Instance,
            _k8sClient,
            _fencingDriver,
            _blacklistManager,
            _eventPublisher,
            _metrics,
            Options.Create(_options),
            _timeProvider);
    }

    private V1Pod CreateTerminatingPod(string name = "mssql-0", string node = "node-1", int terminatingSeconds = 60) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = "default",
            Uid = "pod-uid-1",
            DeletionTimestamp = _timeProvider.GetUtcNow().AddSeconds(-terminatingSeconds).UtcDateTime,
            OwnerReferences =
            [
                new V1OwnerReference { Kind = "StatefulSet", Name = "mssql", Uid = "sts-uid-1" }
            ]
        },
        Spec = new V1PodSpec
        {
            NodeName = node,
            Volumes =
            [
                new V1Volume { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "data-mssql-0" } }
            ]
        }
    };

    private V1Node CreateNotReadyNode(string name = "node-1") => new()
    {
        Metadata = new V1ObjectMeta { Name = name, Uid = "node-uid-1" },
        Status = new V1NodeStatus
        {
            Conditions =
            [
                new V1NodeCondition { Type = "Ready", Status = "False", LastTransitionTime = _timeProvider.GetUtcNow().AddMinutes(-2).UtcDateTime }
            ]
        }
    };

    private V1Node CreateReadyNode(string name = "node-1", int readyForSeconds = 120) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, Uid = "node-uid-1" },
        Status = new V1NodeStatus
        {
            Conditions =
            [
                new V1NodeCondition { Type = "Ready", Status = "True", LastTransitionTime = _timeProvider.GetUtcNow().AddSeconds(-readyForSeconds).UtcDateTime }
            ]
        }
    };

    [Fact]
    public async Task ProcessPod_TerminatingOnNotReadyNode_FencesAndDeletes()
    {
        var pod = CreateTerminatingPod();
        _k8sClient.SetGet("node-1", null, CreateNotReadyNode());
        _k8sClient.SetList(new List<V1VolumeAttachment>());
        _k8sClient.SetGet("data-mssql-0", "default", new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.True(result);
        Assert.Single(_blacklistManager.AddNodeCalls, n => n == "node-1");
        Assert.Single(_fencingDriver.ForceDeleteCalls, p => p == pod);
    }

    [Fact]
    public async Task ProcessPod_BelowTerminatingThreshold_Skips()
    {
        var pod = CreateTerminatingPod(terminatingSeconds: 10); // Below 30s threshold

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(_fencingDriver.ForceDeleteCalls);
    }

    [Fact]
    public async Task ProcessPod_NodeIsReady_Skips()
    {
        var pod = CreateTerminatingPod();
        _k8sClient.SetGet("node-1", null, CreateReadyNode());

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(_fencingDriver.ForceDeleteCalls);
    }

    [Fact]
    public async Task ProcessPod_NotOwnedByStatefulSet_Skips()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = [new V1OwnerReference { Kind = "Deployment", Name = "app" }];

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(_fencingDriver.ForceDeleteCalls);
    }

    [Fact]
    public async Task ProcessPod_NoNodeAssignment_Skips()
    {
        var pod = CreateTerminatingPod();
        pod.Spec.NodeName = null;

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ProcessPod_AlreadyBlacklisted_DeletesButDoesNotCountAsNewFence()
    {
        var pod = CreateTerminatingPod();
        _k8sClient.SetGet("node-1", null, CreateNotReadyNode());
        _blacklistManager.IsBlacklisted = n => n == "node-1";
        _k8sClient.SetList(new List<V1VolumeAttachment>());
        _k8sClient.SetGet("data-mssql-0", "default", new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.False(result); // Not counted as new fence
        Assert.Empty(_blacklistManager.AddNodeCalls);
        Assert.Single(_fencingDriver.ForceDeleteCalls, p => p == pod);
    }

    [Fact]
    public async Task ProcessPod_WithVolumeAttachments_DetachesThenDeletes()
    {
        var pod = CreateTerminatingPod();
        var va = new V1VolumeAttachment
        {
            Metadata = new V1ObjectMeta { Name = "va-1" },
            Spec = new V1VolumeAttachmentSpec
            {
                NodeName = "node-1",
                Attacher = "csi.driver",
                Source = new V1VolumeAttachmentSource { PersistentVolumeName = "pv-data-0" }
            }
        };

        _k8sClient.SetGet("node-1", null, CreateNotReadyNode());
        _k8sClient.SetGet("data-mssql-0", "default", new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        // First list returns the attachment, subsequent lists return none (detached).
        var callCount = 0;
        _k8sClient.SetListFunc<V1VolumeAttachment>(() =>
        {
            callCount++;
            return callCount == 1 ? [va] : [];
        });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        Assert.True(result);
        Assert.Single(_fencingDriver.DetachCalls, c => c.Pod == pod && c.Attachments.Count == 1);
        Assert.Single(_fencingDriver.ForceDeleteCalls, p => p == pod);
    }

    [Fact]
    public async Task ProcessTerminatingPods_ExceedsMaxConcurrent_StopsFencing()
    {
        _options.MaxConcurrentFencing = 1;

        // Already 1 node fenced
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { ["node-0"] = DateTime.UtcNow };

        _k8sClient.SetList(new List<V1Pod> { CreateTerminatingPod() }, InstanceSelector);

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        Assert.Empty(_fencingDriver.ForceDeleteCalls);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeReadyBeyondGrace_Unfences()
    {
        var nodeName = "node-3";
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };
        _k8sClient.SetGet(nodeName, null, CreateReadyNode(nodeName, readyForSeconds: 120));

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        Assert.Single(_blacklistManager.RemoveNodeCalls, n => n == nodeName);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeReadyBelowGrace_DoesNotUnfence()
    {
        var nodeName = "node-3";
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-1).UtcDateTime };
        _k8sClient.SetGet(nodeName, null, CreateReadyNode(nodeName, readyForSeconds: 30)); // Only 30s, need 60s

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        Assert.Empty(_blacklistManager.RemoveNodeCalls);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeStillNotReady_DoesNotUnfence()
    {
        var nodeName = "node-3";
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };
        _k8sClient.SetGet(nodeName, null, CreateNotReadyNode(nodeName));

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        Assert.Empty(_blacklistManager.RemoveNodeCalls);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeDeleted_RemovesFromBlacklist()
    {
        var nodeName = "node-3";
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };
        _k8sClient.SetGet<V1Node>(nodeName, null, null);

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        Assert.Single(_blacklistManager.RemoveNodeCalls, n => n == nodeName);
    }

    [Fact]
    public void IsNodeReady_WithReadyCondition_ReturnsTrue()
    {
        Assert.True(FailoverBreakoutService.IsNodeReady(CreateReadyNode()));
    }

    [Fact]
    public void IsNodeReady_WithNotReadyCondition_ReturnsFalse()
    {
        Assert.False(FailoverBreakoutService.IsNodeReady(CreateNotReadyNode()));
    }

    [Fact]
    public void IsOwnedByStatefulSet_WithStatefulSetOwner_ReturnsTrue()
    {
        Assert.True(FailoverBreakoutService.IsOwnedByStatefulSet(CreateTerminatingPod()));
    }

    [Fact]
    public void IsOwnedByStatefulSet_WithDeploymentOwner_ReturnsFalse()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = [new V1OwnerReference { Kind = "Deployment" }];
        Assert.False(FailoverBreakoutService.IsOwnedByStatefulSet(pod));
    }

    [Fact]
    public void IsOwnedByStatefulSet_NoOwnerReferences_ReturnsFalse()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = null;
        Assert.False(FailoverBreakoutService.IsOwnedByStatefulSet(pod));
    }

    [Fact]
    public async Task FindVolumeAttachments_MatchesByPvNameAndNode()
    {
        var pod = CreateTerminatingPod();
        var pvc = new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } };
        var matchingVa = new V1VolumeAttachment
        {
            Metadata = new V1ObjectMeta { Name = "va-match" },
            Spec = new V1VolumeAttachmentSpec
            {
                NodeName = "node-1",
                Attacher = "csi.driver",
                Source = new V1VolumeAttachmentSource { PersistentVolumeName = "pv-data-0" }
            }
        };
        var otherNodeVa = new V1VolumeAttachment
        {
            Metadata = new V1ObjectMeta { Name = "va-other-node" },
            Spec = new V1VolumeAttachmentSpec
            {
                NodeName = "node-2",
                Attacher = "csi.driver",
                Source = new V1VolumeAttachmentSource { PersistentVolumeName = "pv-data-0" }
            }
        };
        var otherPvVa = new V1VolumeAttachment
        {
            Metadata = new V1ObjectMeta { Name = "va-other-pv" },
            Spec = new V1VolumeAttachmentSpec
            {
                NodeName = "node-1",
                Attacher = "csi.driver",
                Source = new V1VolumeAttachmentSource { PersistentVolumeName = "pv-other" }
            }
        };

        _k8sClient.SetGet("data-mssql-0", "default", pvc);
        _k8sClient.SetList(new List<V1VolumeAttachment> { matchingVa, otherNodeVa, otherPvVa });

        var result = await _service.FindVolumeAttachmentsForPodAsync(pod, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("va-match", result[0].Metadata.Name);
    }

    [Fact]
    public async Task RunLoop_ExecutesBothProcessingAndUnfencing()
    {
        _k8sClient.SetList(new List<V1Pod>(), InstanceSelector);

        await _service.RunLoopAsync(CancellationToken.None);

        Assert.Single(_k8sClient.ListCalls, c => c.Type == "V1Pod" && c.LabelSelector == InstanceSelector);
        Assert.True(_blacklistManager.GetBlacklistedNodesCallCount >= 1);
    }

    // ── Fail closed, bounded pass ─────────────────────────────────

    [Fact]
    public async Task ProcessTerminatingPods_OwnerUnreadable_NotFenced()
    {
        // The pause annotation exists so a human can operate by hand; an owner that cannot be read
        // may be paused, and a fencing cannot be undone.
        ArrangeFenceablePod();
        var pod = CreateTerminatingPod();
        pod.Metadata.Labels = new Dictionary<string, string> { [InstanceSelector] = "mssql" };
        _k8sClient.SetList(new List<V1Pod> { pod }, InstanceSelector);
        _k8sClient.SetGetFailure<MsSqlOperator.Entities.V1Alpha1.SqlInstance>("mssql", "default", new HttpRequestException("timeout"));

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        Assert.Empty(_fencingDriver.ForceDeleteCalls);
        Assert.Empty(_fencingDriver.DetachCalls);
    }

    [Fact]
    public async Task RunPass_ApiCallThatHangs_IsCutShortByTheDeadline()
    {
        ArrangeFenceablePod();
        _options.PassDeadlineSeconds = 1;
        _fencingDriver.Hang = true;

        var completed = await _service.RunPassAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(completed);
        Assert.Single(_fencingDriver.ForceDeleteCalls);
    }

    // ── Relocation, not fencing ────────────────────────────────

    [Fact]
    public async Task ProcessPod_LeaseStillMovingOrDelayNotElapsed_NoRelocation()
    {
        ArrangeFenceablePod();
        _options.RelocationDelaySeconds = 600;
        var pod = CreateTerminatingPod();

        Assert.False(await _service.ProcessPodAsync(pod, CancellationToken.None));
        _timeProvider.Advance(TimeSpan.FromSeconds(300));
        Assert.False(await _service.ProcessPodAsync(pod, CancellationToken.None));
        Assert.Empty(_fencingDriver.ForceDeleteCalls);

        _timeProvider.Advance(TimeSpan.FromSeconds(301)); // the Lease never moved: 601 s
        Assert.True(await _service.ProcessPodAsync(pod, CancellationToken.None));
        Assert.Single(_fencingDriver.ForceDeleteCalls);
    }

    [Fact]
    public async Task ProcessPod_UntrustedStorage_NoRelocation_AHumanDecides()
    {
        ArrangeFenceablePod();
        _options.TrustedFencingCsiDrivers = ["other.driver"];

        Assert.False(await _service.ProcessPodAsync(CreateTerminatingPod(), CancellationToken.None));

        Assert.Empty(_fencingDriver.ForceDeleteCalls);
        Assert.Empty(_fencingDriver.DetachCalls);
        Assert.Contains("RelocationRefused", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ProcessPod_Relocates_WithoutEverTaintingTheNode()
    {
        ArrangeFenceablePod();

        await _service.ProcessPodAsync(CreateTerminatingPod(), CancellationToken.None);

        Assert.Single(_fencingDriver.ForceDeleteCalls);
        Assert.DoesNotContain(_k8sClient.PatchCalls, c => c.Entity is V1Node);
        Assert.Empty(_k8sClient.UpdateCalls.OfType<V1Node>());
    }

    // ── Fencing rate window ──────────────────────────────────────

    private void ArrangeFenceablePod()
    {
        _k8sClient.SetList(new List<V1Pod> { CreateTerminatingPod() }, InstanceSelector);
        _k8sClient.SetList(new List<V1Pod>(), AgSelector);
        _k8sClient.SetGet("node-1", null, CreateNotReadyNode());
        _k8sClient.SetList(new List<V1VolumeAttachment>());
        _k8sClient.SetGet("data-mssql-0", "default", new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });
    }

    [Fact]
    public async Task ProcessTerminating_StaleBlacklistEntriesOutsideWindow_DoNotBlockNewFencing()
    {
        _options.MaxConcurrentFencing = 1;
        _options.FencingRateWindowSeconds = 300;
        ArrangeFenceablePod();
        // Node fenced 10 min ago → outside the 5-min window → must not count toward the limit.
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { ["dead-node"] = _timeProvider.GetUtcNow().AddMinutes(-10).UtcDateTime };

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        Assert.Single(_blacklistManager.AddNodeCalls, n => n == "node-1");
    }

    [Fact]
    public async Task ProcessTerminating_RecentFencingAtLimit_BlocksNewFencing()
    {
        _options.MaxConcurrentFencing = 1;
        _options.FencingRateWindowSeconds = 300;
        ArrangeFenceablePod();
        // Node fenced 1 min ago → inside the window → limit of 1 already reached.
        _blacklistManager.BlacklistedNodes = new Dictionary<string, DateTime> { ["recent-node"] = _timeProvider.GetUtcNow().AddSeconds(-60).UtcDateTime };

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        Assert.Empty(_blacklistManager.AddNodeCalls);
    }
}
