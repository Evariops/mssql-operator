using MsSqlOperator.Core.Maintenance;

namespace MsSqlOperator.Tests.Core;

/// <summary>What the eviction webhook records, and what it answers in Enforce.</summary>
public class EvictionRequestsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static EvictionRecord Record(int lastSeconds = 0, string? by = "admin") => new(T0, by, T0.AddSeconds(lastSeconds));

    private static PublishedVerdict Verdict(VerdictDecision decision, int validUntilSeconds, string reason = "waiting for ag-1 to be synchronized")
        => new(decision, reason, T0.AddSeconds(600), T0.AddSeconds(validUntilSeconds));

    [Fact]
    public void AFirstRequest_IsRecorded_WithItsRequester()
    {
        var record = EvictionRequests.Next(null, "system:node:w-1", T0);

        Assert.Equal(new EvictionRecord(T0, "system:node:w-1", T0), record);
        Assert.True(record!.FromNode);
    }

    [Fact]
    public void ARetry_RewritesNothing_ThenRefreshesTheLastRequest_EveryThirtySeconds()
    {
        Assert.Null(EvictionRequests.Next(Record(), "admin", T0.AddSeconds(5)));
        Assert.Equal(Record(lastSeconds: 30), EvictionRequests.Next(Record(), "admin", T0.AddSeconds(30)));
    }

    [Fact]
    public void ANodeAskingAfterAnotherRequester_TakesOver_FromTheFirstRequest()
    {
        var next = EvictionRequests.Next(Record(), "system:node:w-1", T0.AddSeconds(5));

        Assert.Equal(new EvictionRecord(T0, "system:node:w-1", T0.AddSeconds(5)), next);
        Assert.Equal(T0.AddSeconds(210), EvictionBudget.Default.DeadlineOf(next!.ToRequest("ag-1")));
    }

    [Fact]
    public void ARequestNotRepeatedForTwoMinutes_IsAbandoned_AndTheNextOneStartsANewDelay()
    {
        var later = T0.AddSeconds(30 + 120);

        Assert.True(EvictionRequests.IsAbandoned(Record(lastSeconds: 30), later));
        Assert.Equal(new EvictionRecord(later, "admin", later), EvictionRequests.Next(Record(lastSeconds: 30), "admin", later));
    }

    [Fact]
    public void AValidVerdict_IsTheAnswer_WithTheDelayInTheMessage()
    {
        var delayed = EvictionRequests.Answer("availability group prod/ag", Record(), Verdict(VerdictDecision.Delay, 40), false,
            T0.AddSeconds(12), EvictionBudget.Default);
        var allowed = EvictionRequests.Answer("availability group prod/ag", Record(), Verdict(VerdictDecision.Allow, 40, "the other replicas are synchronized"),
            false, T0.AddSeconds(12), EvictionBudget.Default);

        Assert.False(delayed.Allowed);
        Assert.Equal("mssql-operator, availability group prod/ag: waiting for ag-1 to be synchronized; delayed 12 s of 600 s at most.", delayed.Message);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void AStaleVerdict_OrNone_Delays_UntilTheDeadline()
    {
        var stale = EvictionRequests.Answer("ag", Record(), Verdict(VerdictDecision.Allow, 10), false, T0.AddSeconds(12), EvictionBudget.Default);
        var none = EvictionRequests.Answer("ag", Record(), null, false, T0.AddSeconds(12), EvictionBudget.Default);
        var spent = EvictionRequests.Answer("ag", Record(), Verdict(VerdictDecision.Delay, 700), false, T0.AddSeconds(600), EvictionBudget.Default);

        Assert.False(stale.Allowed);
        Assert.Contains("waiting for the operator's verdict", none.Message);
        Assert.True(spent.Allowed);
        Assert.True(spent.DeadlineReached);
    }

    [Fact]
    public void ARequestThatCouldNotBeRecorded_OrAManualRelease_IsAllowed()
    {
        Assert.True(EvictionRequests.Answer("ag", null, null, false, T0, EvictionBudget.Default).Allowed);
        Assert.True(EvictionRequests.Answer("ag", Record(), Verdict(VerdictDecision.Delay, 40), true, T0.AddSeconds(1), EvictionBudget.Default).Allowed);
    }
}
