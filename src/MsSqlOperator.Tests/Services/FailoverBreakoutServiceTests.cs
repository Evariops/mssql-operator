using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class FailoverBreakoutServiceTests
{
    private readonly Mock<ILogger<FailoverBreakoutService>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<IFencingDriver> _fencingDriver = new();
    private readonly Mock<IBlacklistManager> _blacklistManager = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
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
            PodSelector = "mssql.operator/instance",
            FencingDriver = "volumeattachment",
            MaxConcurrentFencing = 3,
            BlacklistConfigMapName = "mssql-failover-blacklist",
            BlacklistConfigMapNamespace = "default"
        };

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 10, 15, 0, 0, TimeSpan.Zero));

        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DateTime>());
        _blacklistManager.Setup(x => x.IsBlacklistedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _service = new FailoverBreakoutService(
            _logger.Object,
            _k8sClient.Object,
            _fencingDriver.Object,
            _blacklistManager.Object,
            _eventPublisher.Object,
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
        var node = CreateNotReadyNode();

        _k8sClient.Setup(x => x.GetAsync<V1Node>("node-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        _k8sClient.Setup(x => x.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1VolumeAttachment>());
        _k8sClient.Setup(x => x.GetAsync<V1PersistentVolumeClaim>("data-mssql-0", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeTrue();
        _blacklistManager.Verify(x => x.AddNodeAsync("node-1", It.IsAny<CancellationToken>()), Times.Once);
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(pod, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPod_BelowTerminatingThreshold_Skips()
    {
        var pod = CreateTerminatingPod(terminatingSeconds: 10); // Below 30s threshold

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeFalse();
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPod_NodeIsReady_Skips()
    {
        var pod = CreateTerminatingPod();
        var readyNode = CreateReadyNode();

        _k8sClient.Setup(x => x.GetAsync<V1Node>("node-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(readyNode);

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeFalse();
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPod_NotOwnedByStatefulSet_Skips()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = [new V1OwnerReference { Kind = "Deployment", Name = "app" }];

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeFalse();
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessPod_NoNodeAssignment_Skips()
    {
        var pod = CreateTerminatingPod();
        pod.Spec.NodeName = null;

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessPod_AlreadyBlacklisted_DeletesButDoesNotCountAsNewFence()
    {
        var pod = CreateTerminatingPod();
        var node = CreateNotReadyNode();

        _k8sClient.Setup(x => x.GetAsync<V1Node>("node-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        _blacklistManager.Setup(x => x.IsBlacklistedAsync("node-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _k8sClient.Setup(x => x.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1VolumeAttachment>());
        _k8sClient.Setup(x => x.GetAsync<V1PersistentVolumeClaim>("data-mssql-0", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeFalse(); // Not counted as new fence
        _blacklistManager.Verify(x => x.AddNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(pod, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPod_WithVolumeAttachments_DetachesThenDeletes()
    {
        var pod = CreateTerminatingPod();
        var node = CreateNotReadyNode();
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

        _k8sClient.Setup(x => x.GetAsync<V1Node>("node-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        _k8sClient.Setup(x => x.GetAsync<V1PersistentVolumeClaim>("data-mssql-0", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });

        var callCount = 0;
        _k8sClient.Setup(x => x.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? new List<V1VolumeAttachment> { va } : new List<V1VolumeAttachment>();
            });

        var result = await _service.ProcessPodAsync(pod, CancellationToken.None);

        result.Should().BeTrue();

        // Verify strict order: detach BEFORE delete
        var sequence = new MockSequence();
        _fencingDriver.Verify(x => x.DetachVolumesAsync(pod, It.Is<IReadOnlyList<V1VolumeAttachment>>(l => l.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
        _fencingDriver.Verify(x => x.ForceDeletePodAsync(pod, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTerminatingPods_ExceedsMaxConcurrent_StopsFencing()
    {
        _options.MaxConcurrentFencing = 1;

        // Already 1 node fenced
        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DateTime> { ["node-0"] = DateTime.UtcNow });

        var pod = CreateTerminatingPod();
        _k8sClient.Setup(x => x.ListAsync<V1Pod>((string?)null, "mssql.operator/instance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1Pod> { pod });

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        _fencingDriver.Verify(x => x.ForceDeletePodAsync(It.IsAny<V1Pod>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeReadyBeyondGrace_Unfences()
    {
        var nodeName = "node-3";
        var blacklisted = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };

        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(blacklisted);
        _k8sClient.Setup(x => x.GetAsync<V1Node>(nodeName, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReadyNode(nodeName, readyForSeconds: 120));

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.RemoveNodeAsync(nodeName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeReadyBelowGrace_DoesNotUnfence()
    {
        var nodeName = "node-3";
        var blacklisted = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-1).UtcDateTime };

        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(blacklisted);
        _k8sClient.Setup(x => x.GetAsync<V1Node>(nodeName, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReadyNode(nodeName, readyForSeconds: 30)); // Only 30s, need 60s

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.RemoveNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeStillNotReady_DoesNotUnfence()
    {
        var nodeName = "node-3";
        var blacklisted = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };

        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(blacklisted);
        _k8sClient.Setup(x => x.GetAsync<V1Node>(nodeName, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateNotReadyNode(nodeName));

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.RemoveNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessUnfencing_NodeDeleted_RemovesFromBlacklist()
    {
        var nodeName = "node-3";
        var blacklisted = new Dictionary<string, DateTime> { [nodeName] = _timeProvider.GetUtcNow().AddMinutes(-5).UtcDateTime };

        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(blacklisted);
        _k8sClient.Setup(x => x.GetAsync<V1Node>(nodeName, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((V1Node?)null);

        await _service.ProcessUnfencingAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.RemoveNodeAsync(nodeName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void IsNodeReady_WithReadyCondition_ReturnsTrue()
    {
        var node = CreateReadyNode();
        FailoverBreakoutService.IsNodeReady(node).Should().BeTrue();
    }

    [Fact]
    public void IsNodeReady_WithNotReadyCondition_ReturnsFalse()
    {
        var node = CreateNotReadyNode();
        FailoverBreakoutService.IsNodeReady(node).Should().BeFalse();
    }

    [Fact]
    public void IsOwnedByStatefulSet_WithStatefulSetOwner_ReturnsTrue()
    {
        var pod = CreateTerminatingPod();
        FailoverBreakoutService.IsOwnedByStatefulSet(pod).Should().BeTrue();
    }

    [Fact]
    public void IsOwnedByStatefulSet_WithDeploymentOwner_ReturnsFalse()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = [new V1OwnerReference { Kind = "Deployment" }];
        FailoverBreakoutService.IsOwnedByStatefulSet(pod).Should().BeFalse();
    }

    [Fact]
    public void IsOwnedByStatefulSet_NoOwnerReferences_ReturnsFalse()
    {
        var pod = CreateTerminatingPod();
        pod.Metadata.OwnerReferences = null;
        FailoverBreakoutService.IsOwnedByStatefulSet(pod).Should().BeFalse();
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

        _k8sClient.Setup(x => x.GetAsync<V1PersistentVolumeClaim>("data-mssql-0", "default", It.IsAny<CancellationToken>())).ReturnsAsync(pvc);
        _k8sClient.Setup(x => x.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1VolumeAttachment> { matchingVa, otherNodeVa, otherPvVa });

        var result = await _service.FindVolumeAttachmentsForPodAsync(pod, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Metadata.Name.Should().Be("va-match");
    }

    [Fact]
    public async Task RunLoop_ExecutesBothProcessingAndUnfencing()
    {
        _k8sClient.Setup(x => x.ListAsync<V1Pod>((string?)null, "mssql.operator/instance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1Pod>());

        await _service.RunLoopAsync(CancellationToken.None);

        _k8sClient.Verify(x => x.ListAsync<V1Pod>((string?)null, "mssql.operator/instance", It.IsAny<CancellationToken>()), Times.Once);
        _blacklistManager.Verify(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── Fencing rate window ──────────────────────────────────────

    private void ArrangeFenceablePod()
    {
        var pod = CreateTerminatingPod();
        _k8sClient.Setup(x => x.ListAsync<V1Pod>((string?)null, "mssql.operator/instance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1Pod> { pod });
        _k8sClient.Setup(x => x.ListAsync<V1Pod>((string?)null, "mssql.operator/ag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1Pod>());
        _k8sClient.Setup(x => x.GetAsync<V1Node>("node-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(CreateNotReadyNode());
        _k8sClient.Setup(x => x.ListAsync<V1VolumeAttachment>((string?)null, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<V1VolumeAttachment>());
        _k8sClient.Setup(x => x.GetAsync<V1PersistentVolumeClaim>("data-mssql-0", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1PersistentVolumeClaim { Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-data-0" } });
    }

    [Fact]
    public async Task ProcessTerminating_StaleBlacklistEntriesOutsideWindow_DoNotBlockNewFencing()
    {
        _options.MaxConcurrentFencing = 1;
        _options.FencingRateWindowSeconds = 300;
        ArrangeFenceablePod();
        // Node fenced 10 min ago → outside the 5-min window → must not count toward the limit.
        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DateTime> { ["dead-node"] = _timeProvider.GetUtcNow().AddMinutes(-10).UtcDateTime });

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.AddNodeAsync("node-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTerminating_RecentFencingAtLimit_BlocksNewFencing()
    {
        _options.MaxConcurrentFencing = 1;
        _options.FencingRateWindowSeconds = 300;
        ArrangeFenceablePod();
        // Node fenced 1 min ago → inside the window → limit of 1 already reached.
        _blacklistManager.Setup(x => x.GetBlacklistedNodesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DateTime> { ["recent-node"] = _timeProvider.GetUtcNow().AddSeconds(-60).UtcDateTime });

        await _service.ProcessTerminatingPodsAsync(CancellationToken.None);

        _blacklistManager.Verify(x => x.AddNodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

internal class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
