using k8s.Models;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The fleet rollout budget is reserved by compare-and-swap in a Lease.</summary>
public class RolloutBudgetTests
{
    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FakeTimeProvider _clock = new();
    private readonly RolloutBudget _budget;

    public RolloutBudgetTests()
    {
        _budget = new RolloutBudget(_k8s, new OperatorOptions { Namespace = "op", MaxConcurrentRestarts = 1 }, _clock);
    }

    private static V1StatefulSet Unready(string ns, string name) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
        Spec = new V1StatefulSetSpec { Replicas = 1 },
        Status = new V1StatefulSetStatus { ReadyReplicas = 0 },
    };

    [Fact]
    public async Task TwoPassesThatBothSawRoom_OnlyTheFirstStarts()
    {
        // Neither StatefulSet rolls yet: the cluster alone let both through.
        var first = await _budget.MayStartAsync("a", "sql-1", CancellationToken.None);
        var second = await _budget.MayStartAsync("a", "sql-2", CancellationToken.None);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal("a/sql-1", second.HeldBy);
    }

    [Fact]
    public async Task ReservationWhoseRolloutNeverShowed_IsReleased()
    {
        await _budget.MayStartAsync("a", "sql-1", CancellationToken.None);
        _clock.Advance(RolloutBudget.ReservationGrace + TimeSpan.FromSeconds(1));

        Assert.True((await _budget.MayStartAsync("a", "sql-2", CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task StatefulSetBusyForLong_NoLongerHoldsTheFleet_AndIsReported()
    {
        _k8s.SetList<V1StatefulSet>([Unready("a", "sql-1")], OperatorLabels.ManagedBySelector);
        var held = await _budget.MayStartAsync("a", "sql-2", CancellationToken.None);
        _clock.Advance(RolloutBudget.StuckAfter);

        var later = await _budget.MayStartAsync("a", "sql-2", CancellationToken.None);

        Assert.False(held.Allowed);
        Assert.True(later.Allowed);
        Assert.Equal(["a/sql-1"], later.Stuck);
    }

    [Fact]
    public async Task ReservationThatCannotBeWritten_TheRolloutWaits()
    {
        _k8s.StoreWrites = false;
        _k8s.SetGet("mssql-operator-rollout-slots", "op", new V1Lease { Metadata = new V1ObjectMeta { Name = "mssql-operator-rollout-slots" } });
        _k8s.PatchMetadataFailure = new k8s.Autorest.HttpOperationException("409 resourceVersion test failed");

        var decision = await _budget.MayStartAsync("a", "sql-1", CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("mssql-operator-rollout-slots", decision.HeldBy, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyStatefulSet_KeepsTheTimeItWasFirstSeen()
    {
        var then = DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var (next, holders, stuck) = RolloutBudget.Account(
            new Dictionary<string, DateTimeOffset> { ["a/sql-1"] = then, ["a/gone"] = then },
            new HashSet<string> { "a/sql-1" }, "a/sql-2", then.AddMinutes(10));

        Assert.Equal(then, next["a/sql-1"]);
        Assert.False(next.ContainsKey("a/gone"));
        Assert.Equal(["a/sql-1"], holders);
        Assert.Empty(stuck);
    }
}
