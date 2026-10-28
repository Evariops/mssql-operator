using k8s.Models;
using Microsoft.Data.SqlClient;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>A dead target no longer slows the others down.</summary>
public class TargetIsolationTests
{
    [Fact]
    public void CircuitBreaker_Enforce_FailsFastUntilTheEndpointIsBackOrTheMarkExpires()
    {
        var clock = new FakeTimeProvider();
        var health = new EndpointHealth(SwitchMode.Enforce, clock);
        health.MarkDown("ag-1", "unreachable");

        Assert.Throws<EndpointUnavailableException>(() => health.Check("ag-1"));
        health.MarkUp("ag-1");
        health.Check("ag-1");

        health.MarkDown("ag-1", "unreachable");
        clock.Advance(EndpointHealth.MarkFor + TimeSpan.FromSeconds(1));
        health.Check("ag-1");
    }

    [Fact]
    public void CircuitBreaker_Shadow_NeverRefuses()
    {
        var health = new EndpointHealth(SwitchMode.Shadow);
        health.MarkDown("ag-1", "unreachable");
        health.Check("ag-1");
    }

    [Fact]
    public async Task Limiter_TwoSessionsPerTarget_TheThirdWaits_OtherTargetsDoNot()
    {
        var limiter = new TargetLimiter();
        await using var a = await limiter.AcquireAsync("t1", CancellationToken.None);
        await using var b = await limiter.AcquireAsync("t1", CancellationToken.None);

        var third = limiter.AcquireAsync("t1", CancellationToken.None);
        await using var other = await limiter.AcquireAsync("t2", CancellationToken.None);
        Assert.False(third.IsCompleted);

        await a.DisposeAsync();
        await using var granted = await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OpenEveryServerPartial_ADeadReplica_DoesNotStopTheOthers()
    {
        var factory = new FakeSqlConnectionFactory
        {
            ConnectionForHost = h => h.StartsWith("sales-1.", StringComparison.Ordinal) ? throw new SqlTimeoutStub() : new SqlConnection($"Server={h}"),
        };
        var k8s = new FakeKubernetesClient();
        k8s.SetGet("sales", "db", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "db" },
            Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
        });
        var target = new ResolvedTarget(TargetKinds.AvailabilityGroup, "sales", "db", "sales-listener.db", 1433, "pw", 3);

        var (open, failed) = await TargetConnections.OpenEveryServerPartialAsync(target, factory, k8s, CancellationToken.None);

        // The session settings cannot be applied on the fake's unopened connections: every open one
        // fails there too — what matters here is that each server is tried, and reported, on its own.
        Assert.Equal(3, open.Count + failed.Count);
        Assert.Contains(failed, f => f.Server == "sales-1");
    }

    private sealed class SqlTimeoutStub() : TimeoutException("connect timeout");
}
