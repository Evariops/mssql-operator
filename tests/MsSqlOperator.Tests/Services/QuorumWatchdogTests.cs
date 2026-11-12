using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Core.Timing;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Control;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The quorum watchdog and the election shell.</summary>
public class QuorumWatchdogTests
{
    [Fact]
    public void Definition_RunsEveryJSeconds_WithItsModeInTheMarker()
    {
        var job = QuorumWatchdog.Definition("sales", TimingBudget.Default, enforce: false);

        Assert.Equal("mssqlop-watchdog-sales", job.Name);
        var schedule = Assert.Single(job.Schedules);
        Assert.Equal((2, 10), (schedule.SubdayType, schedule.SubdayInterval)); // seconds, J = 10
        Assert.Contains("enforce=False", job.Description);
        Assert.NotEqual(job.Description, QuorumWatchdog.Definition("sales", TimingBudget.Default, enforce: true).Description);
    }

    [Theory]
    [InlineData(false, "@enforce bit = 0")]
    [InlineData(true, "@enforce bit = 1")]
    public void Script_CarriesItsModeAndTStep(bool enforce, string expected)
    {
        var script = QuorumWatchdog.Script("sales", TimingBudget.Default, enforce);

        Assert.Contains(expected, script);
        Assert.Contains("@tstep int = 20", script);
        Assert.Contains("OFFLINE", script);
        Assert.Contains("sp_getapplock", script); // the same lock as the operator's commands
    }

    [Fact]
    public void Script_NoQuorumToLoseAtKZero_AndSeedingDoesNotCountAsLost()
    {
        var script = QuorumWatchdog.Script("sales", TimingBudget.Default, enforce: true);

        // k = 0: the primary commits alone by configuration; k_eff = max(k, 1) stepped it down as soon
        // as its only secondary was missing for 20 s.
        Assert.Contains("@keff int = ISNULL(@k, 0)", script);
        Assert.Contains("@keff >= 1 AND @synced < @keff", script);
        // The quorum is connectivity: a secondary seeding a new database is no longer HEALTHY, and the
        // primary went OFFLINE while a database was being added.
        Assert.DoesNotContain("synchronization_health_desc", script);
        Assert.Contains("is_suspended = 1", script);
    }

    [Fact]
    public void Script_AClockJumpCountsAsOnePeriod()
    {
        var script = QuorumWatchdog.Script("sales", TimingBudget.Default, enforce: true);

        Assert.Contains("@period int = 10", script);
        Assert.Contains("IF @elapsed < 0", script);
        Assert.Contains("ELSE IF @elapsed > 3 * @period", script);
    }

    [Fact]
    public void Observe_OfflineSuspendedAndLsnFromTheHealthRead_UnreachableKeepsItsLastHeartbeat()
    {
        var health = new AgHealthReport(false, null,
            [
                new AgReplicaStateInfo("sales-1", "RESOLVING", "NOT_HEALTHY", "CONNECTED", "ONLINE", false, AvailabilityMode: "SYNCHRONOUS_COMMIT"),
                new AgReplicaStateInfo("sales-2", "SECONDARY", "HEALTHY", "DISCONNECTED", "ONLINE", true, AvailabilityMode: "SYNCHRONOUS_COMMIT"),
            ],
            [
                new AgDatabaseStateInfo("Orders", "sales-2", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, IsSuspended: true, LastHardenedLsn: 42m),
                new AgDatabaseStateInfo("Billing", "sales-2", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, IsSuspended: true, LastHardenedLsn: 7m),
            ],
            [], ["sales-1", "sales-2"], []);
        var states = new Dictionary<string, WatchdogState?> { ["sales-2"] = new("SECONDARY", 5, null, null, 60, "ok") };

        var obs = ElectionShell.Observe(Ag(), health, states, 3, DateTimeOffset.UnixEpoch,
            new Dictionary<string, bool> { ["sales-0"] = true });

        Assert.True(obs.Replica("sales-1")!.Offline);
        var s2 = obs.Replica("sales-2")!;
        Assert.True(s2.Suspended);
        Assert.Equal(42m, s2.HardenedLsn!["Orders"]);
        // Unreachable now, its watchdog ran at the last read: T_vote can prove it stepped down.
        Assert.True(obs.Replica("sales-0")!.WatchdogAlive);
    }

    [Fact]
    public void Script_QuotesTheAgName()
    {
        var script = QuorumWatchdog.Script("x'; DROP TABLE t; --", TimingBudget.Default, enforce: true);
        Assert.Contains("N'x''; DROP TABLE t; --'", script);
    }

    [Theory]
    [InlineData(25, true)]
    [InlineData(31, false)]
    public void Alive_WithinThreePeriods(int age, bool alive)
        => Assert.Equal(alive, QuorumWatchdog.Alive(new WatchdogState("PRIMARY", age, 2, null, null, "ok"), TimingBudget.Default));

    private static SqlAvailabilityGroup Ag(string? level = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "sales",
            NamespaceProperty = "db",
            Uid = "u",
            Annotations = level is null ? null : new Dictionary<string, string> { [MsSqlOperator.Core.Safety.SafetyLevel.Annotation] = level },
        },
        Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
        Status = new SqlAvailabilityGroupStatus { PrimaryReplica = "sales-0" },
    };

    [Fact]
    public async Task Shell_Level2OffByDefault_TouchesNothing()
    {
        var factory = new FakeSqlConnectionFactory();
        var shell = new ElectionShell(factory, new FakeSqlAgentManager(), new OperatorOptions(), null, NullLogger<ElectionShell>.Instance);
        var health = new AgHealthReport(true, "sales-0", [], [], [], ["sales-0", "sales-1"], []);

        var states = await shell.RunAsync(Ag(), health, "pw", "sales-headless", "NoAction", DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Empty(states);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task Shell_Level2TurnedOff_DisablesTheWatchdogsItHadProvisioned()
    {
        // The ceiling back at 1, the watchdog jobs kept running on every replica.
        var factory = new FakeSqlConnectionFactory();
        var agent = new FakeSqlAgentManager { JobId = Guid.NewGuid() };
        var shell = new ElectionShell(factory, agent, new OperatorOptions(), null, NullLogger<ElectionShell>.Instance);
        var ag = Ag();
        ag.Status!.Conditions = [new ConditionStatus { Type = ElectionShell.WatchdogProvisionedCondition, Status = "True", Reason = "Shadow" }];
        var health = new AgHealthReport(true, "sales-0", [], [], [], ["sales-0", "sales-1", "sales-2"], []);

        await shell.RunAsync(ag, health, "pw", "sales-headless", "NoAction", DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(3, agent.Replacements.Count);
        Assert.All(agent.Replacements, d => Assert.False(d.Enabled));
        Assert.Equal("False", ag.Status.Conditions.Single(c => c.Type == ElectionShell.WatchdogProvisionedCondition).Status);
    }

    [Fact]
    public void Observe_TakesTheRolesFromTheHealthReadAndTheLossFromTheWatchdogs()
    {
        var health = new AgHealthReport(false, null,
            [new AgReplicaStateInfo("sales-1", "SECONDARY", "HEALTHY", "DISCONNECTED", "ONLINE", true, AvailabilityMode: "SYNCHRONOUS_COMMIT")],
            [], [], ["sales-1", "sales-2"], []);
        var states = new Dictionary<string, WatchdogState?>
        {
            ["sales-1"] = new("SECONDARY", 5, null, null, 50, "ok"),
            ["sales-2"] = new("SECONDARY", 40, null, null, null, "ok"),
        };

        var obs = ElectionShell.Observe(Ag(), health, states, 3, DateTimeOffset.UnixEpoch);

        var s1 = obs.Replica("sales-1")!;
        Assert.Equal(("SECONDARY", TimeSpan.FromSeconds(50), false, true), (s1.Role, s1.PrimaryDisconnectedFor!.Value, s1.SeesPrimaryConnected, s1.WatchdogAlive));
        Assert.False(obs.Replica("sales-2")!.WatchdogAlive); // 40 s > 3 J
        Assert.False(obs.Replica("sales-0")!.Reachable);
        Assert.Equal("sales-0", obs.LastKnownPrimary);
    }
}

public class ListenerRoutingTests
{
    private static SqlAvailabilityGroup Ag() => new()
    {
        Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "db" },
        Spec = new SqlAvailabilityGroupSpec(),
        Status = new SqlAvailabilityGroupStatus { PrimaryReplica = "sales-1" },
    };

    private static readonly Dictionary<string, string> Labels = new() { ["mssql-operator.evariops.com/ag"] = "sales" };

    [Fact]
    public void Enforce_SelectsThePrimaryByPodName()
    {
        var selector = ListenerRouting.Selector(Ag(), Labels, MsSqlOperator.Core.Safety.SafetyMode.Enforce);
        Assert.Equal("sales-1", selector[ListenerRouting.PodNameLabel]);
        Assert.DoesNotContain("mssql-operator.evariops.com/ag-role", selector.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MsSqlOperator.Core.Safety.SafetyMode.Shadow)]
    public void BelowEnforce_KeepsTheLabelRouting(MsSqlOperator.Core.Safety.SafetyMode? mode)
    {
        var selector = ListenerRouting.Selector(Ag(), Labels, mode);
        Assert.Equal("primary", selector["mssql-operator.evariops.com/ag-role"]);
        Assert.DoesNotContain(ListenerRouting.PodNameLabel, selector.Keys);
    }

    [Fact]
    public async Task Route_WritesOnlyWhenThePrimaryChanged()
    {
        var k8s = new FakeKubernetesClient();
        k8s.SetGet("sales-listener", "db", new V1Service
        {
            Metadata = new V1ObjectMeta { Name = "sales-listener", NamespaceProperty = "db" },
            Spec = new V1ServiceSpec { Selector = new Dictionary<string, string>(Labels) { [ListenerRouting.PodNameLabel] = "sales-0" } },
        });

        Assert.True(await ListenerRouting.RouteAsync(k8s, Ag(), "sales-1", CancellationToken.None));
        Assert.False(await ListenerRouting.RouteAsync(k8s, Ag(), "sales-0", CancellationToken.None));
        Assert.Single(k8s.PatchCalls);
    }
}
