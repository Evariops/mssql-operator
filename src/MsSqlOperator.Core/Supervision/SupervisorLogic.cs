using MsSqlOperator.Core.State;

namespace MsSqlOperator.Core.Supervision;

/// <summary>What one re-evaluation of an AG actor answers to.</summary>
/// <param name="Trigger">The trigger it counts as: the first signal, the state read, or the safety tick.</param>
/// <param name="Coalesced">The signals folded into it.</param>
/// <param name="TickAt">When the last safety tick was, after this re-evaluation.</param>
public sealed record Reevaluation(string Trigger, int Coalesced, DateTimeOffset TickAt);

/// <summary>
/// The decisions of the AG supervisor, without clock nor I/O — the shell (the
/// actor loop) gives the time and the signals, and applies what comes back. A burst of signals
/// is one re-evaluation (rule 3); the safety tick is a full re-evaluation at most every period, armed
/// on the last one (rule 4); the state read stands in when nothing signalled.
/// </summary>
public static class SupervisorLogic
{
    public const string StateRead = "state-read";
    public const string SafetyTickTrigger = "safety-tick";

    /// <summary>The re-evaluation for the signals drained from the mailbox (none: the state read
    /// period elapsed).</summary>
    public static Reevaluation Next(IReadOnlyList<string> drained, DateTimeOffset now, DateTimeOffset lastTick, TimeSpan safetyTick)
    {
        var trigger = drained.Count > 0 ? drained[0] : StateRead;
        var coalesced = Math.Max(0, drained.Count - 1);
        return now - lastTick >= safetyTick
            ? new Reevaluation(SafetyTickTrigger, coalesced, now)
            : new Reevaluation(trigger, coalesced, lastTick);
    }

    /// <summary>The differences between the supervisor's read and the
    /// reference one; a recovered read is no difference.</summary>
    public static IReadOnlyList<Transition> ShadowGaps(AgSnapshot? reference, AgSnapshot? supervisor)
        => AgTransitions.Diff(reference, supervisor).Where(t => t.Kind is not TransitionKind.ReadRecovered).ToList();
}
