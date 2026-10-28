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
    public async Task Limiter_EveryKindSharesTheSlotsOfATarget()
    {
        // Users, jobs, TDE and configurations take their slots under the same key.
        var limiter = new TargetLimiter();
        await using var user = await limiter.ForTargetAsync(TargetKinds.AvailabilityGroup, "db", "sales", CancellationToken.None);
        await using var job = await limiter.ForTargetAsync(TargetKinds.AvailabilityGroup, "db", "sales", CancellationToken.None);

        var tde = limiter.ForTargetAsync(TargetKinds.AvailabilityGroup, "db", "sales", CancellationToken.None);
        await using var instance = await limiter.ForTargetAsync(TargetKinds.Instance, "db", "sales", CancellationToken.None);
        Assert.False(tde.IsCompleted);

        await user.DisposeAsync();
        await using var granted = await tde.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Limiter_DeletedTarget_ItsGatesAreDropped()
    {
        var limiter = new TargetLimiter();
        var deployer = $"deployer/{TargetLimiter.KeyOf(TargetKinds.Instance, "db", "sql")}";
        await using var held = await limiter.AcquireAsync(deployer, CancellationToken.None, slots: 1);

        limiter.ForgetTarget(TargetKinds.Instance, "db", "sql");

        // A target of the same name created later starts from a gate of its own.
        await using var again = await limiter.AcquireAsync(deployer, CancellationToken.None, slots: 1)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Limiter_NoneInjected_NothingToWaitFor()
    {
        TargetLimiter? none = null;
        await using var slot = await none.ForTargetAsync(TargetKinds.Instance, "db", "sql", CancellationToken.None);
        Assert.Same(TargetLimiter.None, slot);
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

        Assert.Equal(["sales-0", "sales-2"], open.Select(o => o.ReplicaName));
        Assert.Equal("sales-1", Assert.Single(failed).Server);
        Assert.All(factory.ProfileCalls, c => Assert.Equal(SqlProfile.DdlFanout, c.Profile));
    }

    private sealed class SqlTimeoutStub() : TimeoutException("connect timeout");
}
