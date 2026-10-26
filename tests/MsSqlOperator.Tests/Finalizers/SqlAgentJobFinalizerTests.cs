using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlAgentJobFinalizerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgentManager _agentManager = new();
    private readonly FakeTargetResolver _targetResolver = new();
    private readonly FakeEventPublisher _events = new();
    private readonly SqlAgentJobFinalizer _finalizer;

    public SqlAgentJobFinalizerTests()
    {
        _finalizer = new SqlAgentJobFinalizer(
            NullLogger<SqlAgentJobFinalizer>.Instance, _k8s, _connFactory, _agentManager, _targetResolver, _events);
        _targetResolver.Resolved = [new(TargetKinds.AvailabilityGroup, "ag", "default", "", 1433, "pw", 3)];
        _k8s.SetGet("ag", "default", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default" },
            Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
        });
    }

    private static SqlAgentJob Job() => new()
    {
        Metadata = new V1ObjectMeta { Name = "nightly", NamespaceProperty = "default" },
        Spec = new SqlAgentJobSpec
        {
            Target = new TargetSelector { AvailabilityGroups = ["ag"] },
            Job = new JobDefinitionSpec { Name = "nightly" },
        },
    };

    [Fact]
    public async Task AgReplicaUnreachable_IsReported_AndDoesNotBlockTheDeletion()
    {
        // opened every replica with no per-replica handling: one dead replica kept the CR
        // Terminating until someone set force-finalize.
        _connFactory.ConnectionForHost = host => host.StartsWith("ag-2.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("unreachable")
            : new Microsoft.Data.SqlClient.SqlConnection($"Server={host}");

        var result = await _finalizer.FinalizeAsync(Job(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _agentManager.DeleteJobCalls.Count);
        Assert.Contains(_events.Calls, c => c.Reason == "SqlArtifactsOrphaned" && c.Message.Contains("ag-2"));
    }

    [Fact]
    public async Task AgReplicaReachable_DeletionError_StillFailsTheFinalization()
    {
        _agentManager.DeleteFails = c => c.DataSource.StartsWith("ag-1.", StringComparison.Ordinal);

        var result = await _finalizer.FinalizeAsync(Job(), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AgReplicas_AreCountedFromTheAvailabilityGroupSpec()
    {
        _targetResolver.Resolved = [new(TargetKinds.AvailabilityGroup, "ag", "default", "", 1433, "pw", null)];

        await _finalizer.FinalizeAsync(Job(), CancellationToken.None);

        Assert.Equal(3, _agentManager.DeleteJobCalls.Count);
    }
}
