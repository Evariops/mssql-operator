using MsSqlOperator.Core.Election;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Core.Timing;

namespace MsSqlOperator.Tests.Core;

/// <summary>The election protocol, on observations.</summary>
public class ElectionCoreTests
{
    private static readonly TimingBudget Budget = TimingBudget.Default;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, decimal> Lsn(decimal a, decimal b = 0) => new() { ["Orders"] = a, ["Billing"] = b };

    private static ReplicaObservation Primary(string name, int synced) => new(name, true, "PRIMARY", SynchronizedSecondaries: synced, HardenedLsn: Lsn(100, 100));

    private static ReplicaObservation Secondary(string name, TimeSpan? lost = null, bool seesPrimary = false, bool suspended = false, decimal lsn = 100, decimal lsn2 = 100)
        => new(name, true, "SECONDARY", SeesPrimaryConnected: seesPrimary, PrimaryDisconnectedFor: lost, Suspended: suspended, HardenedLsn: Lsn(lsn, lsn2));

    private static ReplicaObservation Lost(string name, bool watchdogAlive = true, bool dead = false)
        => new(name, false, WatchdogAlive: watchdogAlive, DeathProof: dead);

    private static AgObservation Ag(params ReplicaObservation[] replicas) => new("sales", 7, 1, replicas, "sales-0", "sales-0", Now);

    [Fact]
    public void Steady_PrimaryWithItsQuorum_NothingToDo()
    {
        var d = ElectionCore.Decide(null, Ag(Primary("sales-0", 2), Secondary("sales-1"), Secondary("sales-2")), Budget);
        Assert.Empty(d.Commands);
    }

    [Fact]
    public void DbaSwitchover_IsAdopted_NotUndone()
    {
        // A DBA failed over by hand; the new primary holds its quorum — the listener follows it.
        var d = ElectionCore.Decide(null, Ag(Secondary("sales-0"), Primary("sales-1", 2), Secondary("sales-2")), Budget);
        Assert.Contains(new Route("sales-1"), d.Commands);
        Assert.DoesNotContain(d.Commands, c => c is TakeOffline);
    }

    [Fact]
    public void TwoPrimaries_TheOneWithoutQuorumGoesOffline()
    {
        var d = ElectionCore.Decide(null, Ag(Primary("sales-0", 0), Primary("sales-1", 1), Secondary("sales-2")), Budget);
        Assert.Contains(d.Commands, c => c is TakeOffline { Replica: "sales-0" });
        Assert.DoesNotContain(d.Commands, c => c is TakeOffline { Replica: "sales-1" });
    }

    [Fact]
    public void OnlyPrimaryWithoutQuorum_IsNeverTakenOfflineByTheOperator()
    {
        var d = ElectionCore.Decide(null, Ag(Primary("sales-0", 0), Lost("sales-1"), Lost("sales-2")), Budget);
        Assert.Empty(d.Commands);
    }

    [Fact]
    public void Veto_ASecondaryStillSeesThePrimary_NoElection()
    {
        // The operator lost the primary, its secondaries did not.
        var d = ElectionCore.Decide(null, Ag(Lost("sales-0"), Secondary("sales-1", seesPrimary: true), Secondary("sales-2", TimeSpan.FromMinutes(5))), Budget);
        Assert.Empty(d.Commands);
        Assert.Contains("veto", d.Reason);
    }

    [Fact]
    public void TooFewSecondariesToIsolate_NeedsAttention()
    {
        // N = 2, k_eff = 1: one isolated secondary is not enough (V + k_eff must exceed N).
        var d = ElectionCore.Decide(null, Ag(Lost("sales-0"), Secondary("sales-1", TimeSpan.FromMinutes(5)), Lost("sales-2")), Budget);
        Assert.Contains(d.Commands, c => c is RaiseNeedsAttention { Reason: "NoElectionQuorum" });
        Assert.DoesNotContain(d.Commands, c => c is Isolate or Promote);
    }

    [Theory]
    [InlineData(30, false)] // below T_vote: the old primary may not have stepped down yet
    [InlineData(45, true)]
    public void OldPrimaryUnreachable_ElectionStartsOnlyAfterTVote(int lostSeconds, bool starts)
    {
        var d = ElectionCore.Decide(null, Ag(Lost("sales-0"),
            Secondary("sales-1", TimeSpan.FromSeconds(lostSeconds)), Secondary("sales-2", TimeSpan.FromSeconds(lostSeconds))), Budget);
        Assert.Equal(starts, d.Commands.OfType<Isolate>().Count() == 2);
    }

    [Fact]
    public void WatchdogUnavailable_TVoteProvesNothing()
    {
        var d = ElectionCore.Decide(null, Ag(Lost("sales-0", watchdogAlive: false),
            Secondary("sales-1", TimeSpan.FromMinutes(5)), Secondary("sales-2", TimeSpan.FromMinutes(5))), Budget);
        Assert.DoesNotContain(d.Commands, c => c is Isolate);
    }

    [Fact]
    public void DeathProof_NoNeedToWaitForTVote()
    {
        var d = ElectionCore.Decide(null, Ag(Lost("sales-0", dead: true), Secondary("sales-1", TimeSpan.FromSeconds(1)), Secondary("sales-2", TimeSpan.FromSeconds(1))), Budget);
        Assert.Equal(2, d.Commands.OfType<Isolate>().Count());
    }

    [Fact]
    public void FullElection_IsolatesPromotesTheMostAdvancedRoutesAndResumesTheOthers()
    {
        var start = ElectionCore.Decide(null, Ag(Lost("sales-0", dead: true), Secondary("sales-1"), Secondary("sales-2")), Budget);
        var isolated = Ag(Lost("sales-0", dead: true), Secondary("sales-1", suspended: true, lsn: 90, lsn2: 95), Secondary("sales-2", suspended: true, lsn: 100, lsn2: 100));

        var promote = ElectionCore.Decide(start.State, isolated, Budget);
        Assert.Contains(new Promote("sales-2", 7, true), promote.Commands);
        Assert.Equal(ElectionStage.Promoting, promote.State!.Stage);

        var promoted = Ag(Lost("sales-0"), Secondary("sales-1", suspended: true), Primary("sales-2", 0));
        var route = ElectionCore.Decide(promote.State, promoted, Budget);
        Assert.Contains(new Route("sales-2"), route.Commands);

        var done = ElectionCore.Decide(route.State, promoted with { RoutedPrimary = "sales-2" }, Budget);
        Assert.Equal(ElectionStage.Done, done.State!.Stage);
        Assert.Contains(new Resume("sales-1", 7), done.Commands);
        Assert.DoesNotContain(new Resume("sales-2", 7), done.Commands);
    }

    [Fact]
    public void NoReplicaAheadOnEveryDatabase_AHumanChooses()
    {
        var start = ElectionCore.Decide(null, Ag(Lost("sales-0", dead: true), Secondary("sales-1"), Secondary("sales-2")), Budget);
        var split = Ag(Lost("sales-0", dead: true), Secondary("sales-1", suspended: true, lsn: 100, lsn2: 90), Secondary("sales-2", suspended: true, lsn: 90, lsn2: 100));

        var d = ElectionCore.Decide(start.State, split, Budget);

        Assert.Contains(d.Commands, c => c is RaiseNeedsAttention { Reason: "NoMostAdvancedReplica" });
        Assert.DoesNotContain(d.Commands, c => c is Promote);
    }

    [Fact]
    public void OldPrimaryBackWithItsQuorumBeforeThePromotion_ElectionAborted()
    {
        var start = ElectionCore.Decide(null, Ag(Lost("sales-0", dead: true), Secondary("sales-1"), Secondary("sales-2")), Budget);
        var back = Ag(Primary("sales-0", 1), Secondary("sales-1", suspended: true), Secondary("sales-2"));

        var d = ElectionCore.Decide(start.State! with { Isolated = ["sales-1"] }, back, Budget);

        Assert.Equal(ElectionStage.Aborted, d.State!.Stage);
        Assert.Contains(new Resume("sales-1", 7), d.Commands);
    }

    [Fact]
    public void TimingBudget_DefaultsAreSafe_AndTheConstraintIsChecked()
    {
        Assert.Empty(TimingBudget.Default.Violations());
        Assert.NotEmpty((TimingBudget.Default with { Vote = TimeSpan.FromSeconds(30) }).Violations());
        Assert.Equal(TimeSpan.FromSeconds(45), TimingBudget.For(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)).Vote);
    }

    [Theory]
    [InlineData(null, 3, 0, 1)]
    [InlineData("2", 3, 2, 3)]
    [InlineData("3", 1, 1, null)]
    [InlineData("bogus", 2, 0, 1)]
    public void SafetyLevel_EffectiveAndShadow(string? annotation, int ceiling, int effective, int? shadow)
    {
        Assert.Equal(effective, SafetyLevel.Effective(annotation, ceiling));
        Assert.Equal(shadow, SafetyLevel.Shadow(effective, ceiling));
    }
}
