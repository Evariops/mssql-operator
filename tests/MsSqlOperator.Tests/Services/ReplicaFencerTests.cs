using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class ReplicaFencerTests
{
    private readonly FakeKubernetesClient _k8s = new();

    private readonly FakeTimeProvider _clock = new();

    private ReplicaFencer CreateFencer(params string[] trustedDrivers) =>
        new(NullLogger<ReplicaFencer>.Instance, _k8s, new FakeFencingDriver(), _clock,
            breakoutOptions: Microsoft.Extensions.Options.Options.Create(new BreakoutOptions { TrustedFencingCsiDrivers = [.. trustedDrivers] }));

    /// <summary>ag-0's data volume, on a CSI driver that attaches, still attached to node-1.</summary>
    private void SeedAttachedStorage(string driver = "csi.example.com")
    {
        _k8s.SetGet("data-ag-0", "default", new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = "data-ag-0", NamespaceProperty = "default" },
            Spec = new V1PersistentVolumeClaimSpec { VolumeName = "pv-0" },
        });
        _k8s.SetGet<V1PersistentVolume>("pv-0", null, new V1PersistentVolume
        {
            Metadata = new V1ObjectMeta { Name = "pv-0" },
            Spec = new V1PersistentVolumeSpec { Csi = new V1CSIPersistentVolumeSource { Driver = driver, VolumeHandle = "h" } },
        });
        _k8s.SetGet<V1CSIDriver>(driver, null, new V1CSIDriver
        {
            Metadata = new V1ObjectMeta { Name = driver },
            Spec = new V1CSIDriverSpec { AttachRequired = true },
        });
        _k8s.SetList<V1VolumeAttachment>([new V1VolumeAttachment
        {
            Metadata = new V1ObjectMeta { Name = "va-0" },
            Spec = new V1VolumeAttachmentSpec { Attacher = driver, NodeName = "node-1", Source = new V1VolumeAttachmentSource { PersistentVolumeName = "pv-0" } },
        }]);
    }

    private void SeedPodOnReadyNode(long terminationGraceSeconds)
    {
        _k8s.SetGet("ag-0", "default", new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "ag-0", NamespaceProperty = "default", Uid = "uid-0" },
            Spec = new V1PodSpec { NodeName = "node-1", TerminationGracePeriodSeconds = terminationGraceSeconds },
        });
        _k8s.SetGet<V1Node>("node-1", null, new V1Node
        {
            Metadata = new V1ObjectMeta { Name = "node-1" },
            Status = new V1NodeStatus { Conditions = [new V1NodeCondition { Type = "Ready", Status = "True" }] },
        });
    }

    [Theory]
    [InlineData(180, 30)] // A frozen primary only stopped at the SIGKILL ending its 180-s grace
    [InlineData(10, 10)]  // a shorter grace period of its own is kept
    public async Task FenceAsync_PodOnReadyNode_DeletesItWithABoundedGracePeriod(long podGrace, int expectedGrace)
    {
        SeedPodOnReadyNode(podGrace);
        var state = new AgFencingStatus();

        await CreateFencer().FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Single(_k8s.DeletePodCalls, c => c.Name == "ag-0" && c.Ns == "default" && c.GracePeriodSeconds == expectedGrace);
        Assert.Equal(ReplicaFencer.GracefulDeletionStage, state.Stage);
        Assert.Equal("uid-0", state.PodUid);
    }

    [Fact]
    public async Task FenceAsync_PodCreatedAfterThePrimaryWasLost_IsAReplacement_TheStorageIsFenced()
    {
        // The original object went without any kubelet confirming the stop; deleting its replacement,
        // stuck in ContainerCreating on a healthy node, proved nothing about the original.
        SeedPodOnReadyNode(30);
        var pod = await _k8s.GetAsync<V1Pod>("ag-0", "default");
        pod!.Metadata.CreationTimestamp = _clock.GetUtcNow().UtcDateTime;
        SeedAttachedStorage();
        var state = new AgFencingStatus { PrimaryLostAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1) };

        var result = await CreateFencer().FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(_k8s.DeletePodCalls);
        Assert.Equal(ReplicaFencer.StorageDetachStage, state.Stage);
        Assert.Single(_k8s.Deleted<V1VolumeAttachment>(), a => a.Metadata.Name == "va-0");
        Assert.True(result.InProgress);
    }

    [Fact]
    public async Task FenceAsync_NodeStillReadyButItsLeaseIsStale_TheStorageIsFenced()
    {
        // Ready is updated 40 to 50 s after a node loss: the graceful deletion sent to a dead kubelet
        // cost a full grace period before the detachment.
        SeedPodOnReadyNode(30);
        SeedAttachedStorage();
        _k8s.SetGet("node-1", "kube-node-lease", new V1Lease
        {
            Metadata = new V1ObjectMeta { Name = "node-1", NamespaceProperty = "kube-node-lease" },
            Spec = new V1LeaseSpec { RenewTime = _clock.GetUtcNow().UtcDateTime - ReplicaFencer.NodeLeaseStaleAfter - TimeSpan.FromSeconds(5) },
        });
        var state = new AgFencingStatus();

        await CreateFencer().FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(_k8s.DeletePodCalls);
        Assert.Equal(ReplicaFencer.StorageDetachStage, state.Stage);
    }

    [Fact]
    public async Task FenceAsync_FreshLease_GracefulDeletionAsBefore()
    {
        SeedPodOnReadyNode(30);
        _k8s.SetGet("node-1", "kube-node-lease", new V1Lease
        {
            Metadata = new V1ObjectMeta { Name = "node-1", NamespaceProperty = "kube-node-lease" },
            Spec = new V1LeaseSpec { RenewTime = _clock.GetUtcNow().UtcDateTime.AddSeconds(-5) },
        });
        var state = new AgFencingStatus();

        await CreateFencer().FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(ReplicaFencer.GracefulDeletionStage, state.Stage);
    }

    [Fact]
    public async Task FenceAsync_DriverOutsideTheTrustedList_ProvesNothing()
    {
        // The breakout trusted only the drivers of its list; the failover fencing trusted every
        // attaching driver, including one whose detachment leaves the node its access.
        SeedAttachedStorage("nfs.example.com");
        var state = new AgFencingStatus();

        var result = await CreateFencer("csi.example.com").FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.False(result.InProgress);
        Assert.Contains("nfs.example.com", result.Detail);
        Assert.Empty(_k8s.Deleted<V1VolumeAttachment>());
    }

    [Fact]
    public async Task FenceAsync_PodObjectGone_AttachmentStillThere_IsDetachedWithoutFailing()
    {
        // The request named the pod object, which no longer existed: a NullReferenceException on
        // every pass, and the failover failed for as long as the attachment stayed.
        SeedAttachedStorage();
        var state = new AgFencingStatus();

        var result = await CreateFencer().FenceAsync("ag-0", "default", state, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(result.InProgress);
        Assert.Single(_k8s.Deleted<V1VolumeAttachment>(), a => a.Metadata.Name == "va-0");
    }
}
