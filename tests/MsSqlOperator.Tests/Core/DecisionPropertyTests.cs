using MsSqlOperator.Core.Election;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Core;

/// <summary>
/// Properties of the decision functions, over many generated cases (fixed seeds: a failure
/// is reproducible). The unit tests pin examples; these state what must hold for every input.
/// </summary>
public class DecisionPropertyTests
{
    private const int Cases = 500;

    [Fact]
    public void MostAdvanced_IsAtLeastAsAdvancedAsEveryCandidate_OnEveryDatabase_OrThereIsNone()
    {
        var random = new Random(20261028);
        for (var i = 0; i < Cases; i++)
        {
            var dbs = Enumerable.Range(0, random.Next(1, 5)).Select(d => $"db{d}").ToList();
            var replicas = Enumerable.Range(0, random.Next(1, 6)).Select(r => new ReplicaObservation(
                $"r{r}", Reachable: true, Role: "SECONDARY",
                HardenedLsn: dbs.ToDictionary(d => d, _ => (decimal)random.Next(0, 4)))).ToList();
            var candidates = replicas.Where(_ => random.Next(2) == 0).Select(r => r.Name).ToList();
            var obs = new AgObservation("ag", 1, 1, replicas, null, null, DateTimeOffset.UnixEpoch);

            var chosen = ElectionCore.MostAdvanced(obs, candidates);

            bool Dominates(ReplicaObservation r) => dbs.All(d => candidates.All(c => r.HardenedLsn![d] >= replicas.Single(o => o.Name == c).HardenedLsn![d]));
            if (chosen is null)
                Assert.DoesNotContain(replicas.Where(r => candidates.Contains(r.Name)), Dominates);
            else
            {
                Assert.Contains(chosen, candidates);
                Assert.True(Dominates(replicas.Single(r => r.Name == chosen)), $"case {i}: {chosen} is not the most advanced");
            }
        }
    }

    [Fact]
    public void Policy_ADestructiveActionNeverRunsOnTheOperatorsOwnWithoutApprovalOrPreAuthorization()
    {
        var random = new Random(31);
        var types = Enum.GetValues<ActionType>();
        var intents = Enum.GetValues<ActuationIntent>();
        var modes = new[] { "Observe", "Plan", "Apply" };
        for (var i = 0; i < Cases * 2; i++)
        {
            var clock = new FakeTimeProvider();
            var policy = new ActuationPolicy(new AutomationOptions(), clock);
            var request = new ActionRequest(types[random.Next(types.Length)], "ag/sales", "7", "case", $"{i}");
            var intent = intents[random.Next(intents.Length)];
            var approved = random.Next(3) == 0;
            var preAuthorized = random.Next(3) == 0;
            var automation = new AutomationSpec
            {
                Mode = modes[random.Next(modes.Length)],
                PreAuthorized = preAuthorized ? [request.Type.ToString()] : null,
            };

            var decision = policy.Decide(request, "ag/sales", automation, approved ? [request.Id] : [], intent);

            if (ActionCatalog.AlwaysDenied(request.Type))
                Assert.Equal(DecisionKind.Denied, decision.Kind);
            if (automation.Mode != "Apply")
                Assert.False(decision.Proceeds, $"case {i}: {decision.Kind} in mode {automation.Mode}");
            if (decision.Proceeds && request.Risk == RiskClass.Destructive)
                Assert.True(intent != ActuationIntent.Operator || approved || preAuthorized,
                    $"case {i}: {request.Type} ran on the operator's own ({decision.Reason})");
        }
    }

    [Fact]
    public void Election_StagesOnlyMoveForward_WhateverTheObservations()
    {
        // state machine, as the election drives it: from any stage and any observation, the
        // next stage is the same or a later one (or Aborted/Done) — never an earlier one.
        var random = new Random(77);
        var order = new[] { ElectionStage.Planned, ElectionStage.Isolating, ElectionStage.Promoting, ElectionStage.Routing, ElectionStage.Done };
        for (var i = 0; i < Cases; i++)
        {
            var stage = order[random.Next(order.Length - 1)];
            var replicas = Enumerable.Range(0, 3).Select(r => new ReplicaObservation($"ag-{r}", Reachable: random.Next(4) > 0,
                Role: r == 0 ? (random.Next(2) == 0 ? "PRIMARY" : "RESOLVING") : (random.Next(3) == 0 ? "PRIMARY" : "SECONDARY"),
                Offline: random.Next(4) == 0, SeesPrimaryConnected: random.Next(3) == 0,
                PrimaryDisconnectedFor: TimeSpan.FromSeconds(random.Next(0, 120)), Suspended: random.Next(2) == 0,
                HardenedLsn: new Dictionary<string, decimal> { ["db"] = random.Next(0, 3) }, WatchdogAlive: random.Next(5) > 0,
                DeathProof: random.Next(5) == 0)).ToList();
            var obs = new AgObservation("ag", 9, 1, replicas, random.Next(2) == 0 ? "ag-1" : "ag-0", "ag-0", DateTimeOffset.UnixEpoch);
            var state = new ElectionState("e1", stage, "ag-0", "ag-1", ["ag-1", "ag-2"], 9, obs.Now, obs.Now);

            var decision = ElectionCore.Decide(state, obs, MsSqlOperator.Core.Timing.TimingBudget.Default);

            if (decision.State is { } next && next.Stage is not (ElectionStage.Aborted or ElectionStage.Done))
                Assert.True(Array.IndexOf(order, next.Stage) >= Array.IndexOf(order, stage),
                    $"case {i}: {stage} went back to {next.Stage} ({decision.Reason})");
        }
    }

    [Fact]
    public void LosslessTarget_OnlyWhenEveryCopyThereIsSynchronizedAndReady_OrStillSeeding()
    {
        // A lossless failover target holds every committed transaction: each of its databases is
        // SYNCHRONIZED and failover-ready, or has never been synchronized at all (seeding).
        var random = new Random(4242);
        var states = new[] { "SYNCHRONIZED", "SYNCHRONIZING", "NOT SYNCHRONIZING", "NOT_SYNCHRONIZING", "INITIALIZING", "REVERTING" };
        var health = new[] { "HEALTHY", "PARTIALLY_HEALTHY", "NOT_HEALTHY" };
        for (var i = 0; i < Cases; i++)
        {
            var target = new MsSqlOperator.Services.SqlServer.AgReplicaStateInfo("ag-1", "SECONDARY", health[random.Next(health.Length)],
                "CONNECTED", "ONLINE", random.Next(2) == 0);
            var dbs = Enumerable.Range(0, random.Next(0, 4)).Select(d => new MsSqlOperator.Services.SqlServer.AgDatabaseStateInfo(
                $"db{d}", random.Next(4) == 0 ? "ag-2" : "ag-1", "SECONDARY", states[random.Next(states.Length)], random.Next(2) == 0, 0, 0)).ToList();
            var report = new MsSqlOperator.Services.Health.AgHealthReport(true, "ag-0", [target], dbs, [], ["ag-0", "ag-1"], []);

            var lossless = MsSqlOperator.Services.SqlServer.AgFailoverOrchestrator.IsLosslessTarget(target, report, out _);

            var own = dbs.Where(d => d.ReplicaName == "ag-1").ToList();
            static bool Seeding(string s) => s is "INITIALIZING" or "REVERTING";
            if (lossless)
            {
                Assert.All(own, d => Assert.True(Seeding(d.SynchronizationState) || (d.IsFailoverReady && d.SynchronizationState == "SYNCHRONIZED"),
                    $"case {i}: {d.DatabaseName} is {d.SynchronizationState}, ready={d.IsFailoverReady}"));
                Assert.NotEqual("NOT_HEALTHY", target.SynchronizationHealth);
                if (own.Count == 0)
                    Assert.True(target.IsFailoverReady);
            }
        }
    }
}
