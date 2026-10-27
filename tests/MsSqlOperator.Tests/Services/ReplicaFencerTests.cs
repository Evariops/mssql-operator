using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class ReplicaFencerTests
{
    private readonly FakeKubernetesClient _k8s = new();

    private ReplicaFencer CreateFencer() =>
        new(NullLogger<ReplicaFencer>.Instance, _k8s, new FakeFencingDriver(), new FakeTimeProvider());

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
}
