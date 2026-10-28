using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Core.Timing;
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
            Name = "sales", NamespaceProperty = "db", Uid = "u",
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
