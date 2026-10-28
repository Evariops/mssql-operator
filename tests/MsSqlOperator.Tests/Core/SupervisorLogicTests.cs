using MsSqlOperator.Core.Supervision;

namespace MsSqlOperator.Tests.Core;

/// <summary>The supervisor's decisions, without clock nor I/O.</summary>
public class SupervisorLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    [Fact]
    public void ABurst_IsOneReevaluation_UnderItsFirstTrigger()
    {
        var next = SupervisorLogic.Next(["pod-event", "transition", "reconcile"], T0.AddSeconds(5), T0, Tick);

        Assert.Equal(("pod-event", 2, T0), (next.Trigger, next.Coalesced, next.TickAt));
    }

    [Fact]
    public void NoSignal_IsTheStateRead()
        => Assert.Equal(SupervisorLogic.StateRead, SupervisorLogic.Next([], T0.AddSeconds(10), T0, Tick).Trigger);

    [Fact]
    public void ThePeriodElapsed_IsTheSafetyTick_ReArmedNow()
    {
        var now = T0 + Tick;
        var next = SupervisorLogic.Next(["pod-event"], now, T0, Tick);

        Assert.Equal((SupervisorLogic.SafetyTickTrigger, now), (next.Trigger, next.TickAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void TheDecision_DependsOnTheCountNotTheOrder(int extra)
    {
        // I9: the same signals, in another order, fold into one re-evaluation of the same size.
        var signals = Enumerable.Range(0, extra + 1).Select(i => $"s{i}").ToList();
        var forward = SupervisorLogic.Next(signals, T0.AddSeconds(1), T0, Tick);
        var reversed = SupervisorLogic.Next(signals.AsEnumerable().Reverse().ToList(), T0.AddSeconds(1), T0, Tick);

        Assert.Equal(forward.Coalesced, reversed.Coalesced);
        Assert.Equal(forward.TickAt, reversed.TickAt);
    }
}
