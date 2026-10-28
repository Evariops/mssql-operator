using MsSqlOperator.Core.State;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Core;

/// <summary>Transitions between state reads, and the state store.</summary>
public class AgTransitionsTests
{
    private static AgSnapshot Snapshot(string? primary, bool r1Connected = true, bool dbSuspended = false, string role1 = "SECONDARY")
        => new("sales", DateTimeOffset.UnixEpoch, primary, 1,
            [new ReplicaState("sales-0", "PRIMARY", true, "HEALTHY", "SYNCHRONOUS_COMMIT"), new ReplicaState("sales-1", role1, r1Connected, "HEALTHY", "SYNCHRONOUS_COMMIT")],
            [new DatabaseState("Orders", "sales-1", "SYNCHRONIZED", dbSuspended)]);

    [Fact]
    public void NothingChanged_NoTransition() => Assert.Empty(AgTransitions.Diff(Snapshot("sales-0"), Snapshot("sales-0")));

    [Fact]
    public void EveryChange_IsATransition()
    {
        var transitions = AgTransitions.Diff(Snapshot("sales-0"), Snapshot("sales-1", r1Connected: false, dbSuspended: true, role1: "PRIMARY"));

        Assert.Equal(
            [TransitionKind.PrimaryChanged, TransitionKind.RoleChanged, TransitionKind.ReplicaDisconnected, TransitionKind.DatabaseSuspended],
            transitions.Select(t => t.Kind));
    }

    [Fact]
    public void AFailedRead_IsItselfASignal()
    {
        Assert.Equal(TransitionKind.ReadFailed, Assert.Single(AgTransitions.Diff(Snapshot("sales-0"), null)).Kind);
        Assert.Equal(TransitionKind.ReadRecovered, Assert.Single(AgTransitions.Diff(null, Snapshot("sales-0"))).Kind);
    }

    [Fact]
    public void Store_PrimaryIsAHintOnlyWhileFresh()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UnixEpoch);
        var store = new AgStateStore(clock);
        store.Update("db", "sales", Snapshot("sales-0"));

        Assert.Equal("sales-0", store.FreshPrimary("db", "sales"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(store.FreshPrimary("db", "sales"));
    }

    [Fact]
    public void FromHealth_TwoPrimariesAreNoHint()
    {
        var health = new AgHealthReport(false, "sales-0", [], [], [], ["sales-0", "sales-1"], [], DetectedPrimaries: ["sales-0", "sales-1"]);
        Assert.Null(AgStateStore.FromHealth("sales", health, DateTimeOffset.UnixEpoch).Primary);
    }
}
