using MsSqlOperator.Core.Election;
using MsSqlOperator.Core.Timing;

namespace MsSqlOperator.Tests.Core.Simulation;

/// <summary>
/// The election protocol under injected faults, in virtual time. Fixed seeds on
/// every build; MSSQLOP_SIM_SEEDS=&lt;n&gt; runs n random seeds (nightly). A failure prints its seed.
/// </summary>
public class SimulationTests
{
    public static TheoryData<int> FixedSeeds => [.. Enumerable.Range(1, 200)];

    [Theory]
    [MemberData(nameof(FixedSeeds))]
    public void RandomFault_NoInvariantIsViolated(int seed)
    {
        var sim = new AgSimulator(seed, replicas: 3 + (seed % 2) * 2);
        var scenario = sim.RandomScenario();
        Assert.True(sim.Violations.Count == 0, $"{scenario}: {string.Join("\n", sim.Violations)}");
    }

    [Fact]
    public void ManyRandomSeeds_WhenAsked()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("MSSQLOP_SIM_SEEDS"), out var n) ? n : 0;
        var start = Environment.TickCount;
        for (var i = 0; i < count; i++)
        {
            var seed = start + i;
            var sim = new AgSimulator(seed, replicas: 3 + (i % 2) * 2);
            var scenario = sim.RandomScenario();
            Assert.True(sim.Violations.Count == 0, $"seed {seed} ({scenario}): {string.Join("\n", sim.Violations)}");
        }
    }

    [Fact]
    public void PrimaryIsolated_FromEveryone_ElectionWithoutLosingAnAcknowledgedCommit()
    {
        // Cut from the operator and the secondaries — the clients still reach it, but it cannot
        // acknowledge alone, steps down in T_step, and the most advanced secondary is promoted.
        var sim = new AgSimulator(1);
        sim.Run(30);
        sim.Isolate("r0");
        sim.CutFromOperator("r0");
        sim.Run(150);

        Assert.Empty(sim.Violations);
        Assert.True(sim.Replicas[0].Offline, "the old primary stepped down");
        Assert.NotEqual("r0", sim.Routed);
        Assert.Equal(1, sim.Elections);
    }

    [Fact]
    public void UnsafeBudget_TVoteShorterThanTheStepDown_IsCaughtByTheSimulation()
    {
        // The simulator must be able to fail: with T_vote below T_step + J + S, a new primary is
        // promoted while the old one, cut from everyone, still serves (I10).
        var unsafeBudget = TimingBudget.Default with { StepDown = TimeSpan.FromSeconds(120), Vote = TimeSpan.FromSeconds(20) };
        Assert.NotEmpty(unsafeBudget.Violations());
        var sim = new AgSimulator(5, budget: unsafeBudget);
        sim.Run(30);
        sim.Isolate("r0");
        sim.CutFromOperator("r0");
        // The clients still reach the old primary through its pod: route them there for the test.
        sim.Run(100);

        Assert.Contains(sim.Violations, v => v.Contains("I10"));
    }

    [Fact]
    public void ShortPartition_BelowTStep_NoElection()
    {
        // A cut shorter than T_step goes unnoticed.
        var sim = new AgSimulator(2);
        sim.Run(30);
        sim.Isolate("r0");
        sim.Run(8);
        sim.Heal();
        sim.Run(120);

        Assert.Empty(sim.Violations);
        Assert.Equal(0, sim.Elections);
        Assert.False(sim.Replicas[0].Offline);
    }

    [Fact]
    public void OperatorAbsent_TheDataPlaneDoesNotNotice()
    {
        // I11: thirty minutes without the operator, no other fault — nothing changes.
        var sim = new AgSimulator(3) { OperatorPresent = false };
        sim.Run(1800);

        Assert.Empty(sim.Violations);
        Assert.Equal(0, sim.Elections);
        Assert.True(sim.Acknowledged.Count > 1700);
    }

    [Fact]
    public void OperatorCutFromThePrimaryOnly_SecondariesVeto()
    {
        // Nothing to do — the secondaries still see the primary.
        var sim = new AgSimulator(4);
        sim.Run(30);
        sim.CutFromOperator("r0");
        sim.Run(300);

        Assert.Empty(sim.Violations);
        Assert.Equal(0, sim.Elections);
    }

    [Fact]
    public void Decide_DependsOnNeitherTheOrderNorTheDuplicationOfObservations()
    {
        // I9: the same observed state gives the same decision, however it arrived.
        var random = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var replicas = Enumerable.Range(0, 3 + random.Next(3)).Select(r => new ReplicaObservation($"r{r}", random.Next(4) > 0,
                random.Next(3) switch { 0 => "PRIMARY", 1 => "SECONDARY", _ => null }, random.Next(5) == 0, true,
                random.Next(2) == 0, random.Next(2) == 0 ? TimeSpan.FromSeconds(random.Next(120)) : null, random.Next(3),
                random.Next(2) == 0, new Dictionary<string, decimal> { ["db"] = random.Next(10) }, random.Next(4) > 0, random.Next(6) == 0)).ToList();
            var obs = new AgObservation("ag", 3, 1, replicas, "r0", "r0", DateTimeOffset.UnixEpoch);
            var shuffled = obs with { Replicas = replicas.OrderBy(_ => random.Next()).ToList() };

            var a = ElectionCore.Decide(null, obs, TimingBudget.Default);
            var b = ElectionCore.Decide(null, shuffled, TimingBudget.Default);
            var again = ElectionCore.Decide(null, obs, TimingBudget.Default);

            Assert.Equal(a.Commands, b.Commands);
            Assert.Equal(a.Commands, again.Commands);
        }
    }

    [Fact]
    public void Decide_NeverIsolatesWithoutEnoughSecondaries_NorAgainstAVeto()
    {
        // V + k_eff > N and no veto, on every generated observation that starts an election.
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var n = 2 + random.Next(4);
            var replicas = new List<ReplicaObservation> { new("r0", false, WatchdogAlive: random.Next(3) > 0, DeathProof: random.Next(4) == 0) };
            replicas.AddRange(Enumerable.Range(1, n).Select(r => new ReplicaObservation($"r{r}", random.Next(4) > 0, "SECONDARY",
                SeesPrimaryConnected: random.Next(6) == 0, PrimaryDisconnectedFor: TimeSpan.FromSeconds(random.Next(90)))));
            var obs = new AgObservation("ag", 1, 1 + random.Next(2), replicas, "r0", "r0", DateTimeOffset.UnixEpoch);

            var d = ElectionCore.Decide(null, obs, TimingBudget.Default);
            var isolated = d.Commands.OfType<Isolate>().Count();
            if (isolated == 0)
                continue;
            Assert.True(isolated + obs.EffectiveK > n, $"V={isolated} k={obs.EffectiveK} N={n}");
            Assert.DoesNotContain(replicas, r => r.Reachable && r.SeesPrimaryConnected);
        }
    }
}
