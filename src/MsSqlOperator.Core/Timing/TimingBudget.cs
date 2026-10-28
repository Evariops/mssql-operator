namespace MsSqlOperator.Core.Timing;

/// <summary>
/// Every delay of the high-availability chain, in one object, and the constraints that make
/// it safe. Each clock only measures its own durations: the primary measures T_step, each secondary
/// its own disconnection — never a comparison of clocks between machines.
/// </summary>
/// <param name="SessionTimeout">S: SESSION_TIMEOUT of the AG — how long a cut between replicas takes to be seen.</param>
/// <param name="WatchdogPeriod">J: period of the watchdog job of each replica.</param>
/// <param name="StepDown">T_step: how long a primary stays without its quorum before it takes itself offline.</param>
/// <param name="Vote">T_vote: how long a secondary must have lost the primary before it is isolated for an election.</param>
/// <param name="ClockDriftMargin">Margin for the drift between the clocks that measure T_step and T_vote.</param>
/// <param name="StateRead">H: period of the operator's state read of an AG.</param>
/// <param name="SafetyTick">Tick: full re-evaluation of an AG.</param>
public sealed record TimingBudget(
    TimeSpan SessionTimeout,
    TimeSpan WatchdogPeriod,
    TimeSpan StepDown,
    TimeSpan Vote,
    TimeSpan ClockDriftMargin,
    TimeSpan StateRead,
    TimeSpan SafetyTick)
{
    /// <summary>The defaults: S 10 s, J 10 s, T_step 20 s, T_vote 45 s, margin 5 s, H 10 s, tick 60 s.</summary>
    public static TimingBudget Default { get; } = new(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

    /// <summary>The smallest T_vote that keeps I10: T_step + J + S + margin.</summary>
    public TimeSpan MinimumVote => StepDown + WatchdogPeriod + SessionTimeout + ClockDriftMargin;

    /// <summary>
    /// The violated constraints, empty when the budget is safe. I10: the old primary is offline
    /// (its watchdog stepped it down) before a new one accepts a write — so T_vote must cover the time
    /// the cut takes to be seen (S), the time without quorum before the step-down (T_step), the
    /// watchdog's own period (J) and the clock drift.
    /// </summary>
    public IReadOnlyList<string> Violations()
    {
        var violations = new List<string>();
        if (Vote < MinimumVote)
            violations.Add($"T_vote ({Vote.TotalSeconds} s) < T_step + J + S + margin ({MinimumVote.TotalSeconds} s): " +
                           "a new primary could accept writes before the old one has stepped down (I10).");
        if (StepDown <= WatchdogPeriod)
            violations.Add($"T_step ({StepDown.TotalSeconds} s) <= J ({WatchdogPeriod.TotalSeconds} s): the watchdog could step a primary " +
                           "down on a single observation.");
        if (SessionTimeout <= TimeSpan.Zero || WatchdogPeriod <= TimeSpan.Zero || StateRead <= TimeSpan.Zero || SafetyTick <= TimeSpan.Zero)
            violations.Add("S, J, H and the tick must be positive.");
        if (SafetyTick < StateRead)
            violations.Add($"the safety tick ({SafetyTick.TotalSeconds} s) is shorter than the state read ({StateRead.TotalSeconds} s).");
        return violations;
    }

    /// <summary>The budget for a given step-down delay and session timeout, T_vote derived (rounded
    /// up to the second).</summary>
    public static TimingBudget For(TimeSpan sessionTimeout, TimeSpan stepDown, TimeSpan? watchdogPeriod = null, TimeSpan? margin = null)
    {
        var j = watchdogPeriod ?? Default.WatchdogPeriod;
        var m = margin ?? Default.ClockDriftMargin;
        var vote = TimeSpan.FromSeconds(Math.Ceiling((stepDown + j + sessionTimeout + m).TotalSeconds));
        return Default with { SessionTimeout = sessionTimeout, StepDown = stepDown, WatchdogPeriod = j, ClockDriftMargin = m, Vote = vote };
    }
}
