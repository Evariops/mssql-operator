namespace MsSqlOperator.Core.Election;

using MsSqlOperator.Core.Timing;

/// <summary>The stages of an election in the control registry.</summary>
public enum ElectionStage { Planned, Isolating, Promoting, Routing, Done, Aborted }

/// <summary>The election in progress, as the registry holds it (its intent).</summary>
public sealed record ElectionState(string Id, ElectionStage Stage, string From, string? To, IReadOnlyList<string> Isolated,
    long Epoch, DateTimeOffset StartedAt, DateTimeOffset? IsolatedAt = null, DateTimeOffset? PromotingSince = null);

/// <summary>A command of the core; the shell executes it and reports the result as an observation.</summary>
public abstract record Command;
public sealed record RecordIntent(ElectionState State) : Command;
public sealed record ClearIntent(string Id) : Command;
/// <summary>Suspend data movement on every database of the AG on the replica, under applock and epoch,
/// refused by the batch itself when the replica still sees the primary connected.</summary>
public sealed record Isolate(string Replica, long Epoch) : Command;
public sealed record Resume(string Replica, long Epoch) : Command;
public sealed record Promote(string Replica, long Epoch, bool AllowDataLoss) : Command;
public sealed record Demote(string Replica, long Epoch) : Command;
public sealed record TakeOffline(string Replica, long Epoch, string Why) : Command;
public sealed record Route(string Replica) : Command;
public sealed record ConfirmRole(string Replica) : Command;
public sealed record RaiseNeedsAttention(string Reason, string Message) : Command;
public sealed record Report(string Reason, string Message) : Command;

/// <summary>What the core decided: the next state of the election (null: none), the commands, and why.</summary>
public sealed record Decision(ElectionState? State, IReadOnlyList<Command> Commands, string Reason)
{
    public static Decision Nothing(ElectionState? state, string reason) => new(state, [], reason);
}

/// <summary>
/// Legitimacy, adoption and elections, as a pure function of the election in progress and of
/// a fresh observation of the AG. The legitimate primary is the one that holds its quorum (at least
/// k_eff synchronized secondaries); an election promotes only when V + k_eff &gt; N — enough isolated
/// secondaries that no write the old primary acknowledged can be missing from all of them — and only
/// once the old primary is known not to serve: observed OFFLINE, dead by proof, or stepped down by its
/// own watchdog (alive at the last read, every isolated secondary cut from it for T_vote).
/// </summary>
public static class ElectionCore
{
    /// <summary>How long the isolation may take before a human is called: it never ended when a
    /// secondary stayed reachable but refused it, and nothing said so.</summary>
    public static TimeSpan IsolationDeadline(TimingBudget budget) => 2 * budget.Vote + budget.SafetyTick;

    /// <summary>How long a promotion may stay unconfirmed: it was re-issued, with data loss allowed,
    /// on every evaluation, for ever.</summary>
    public static TimeSpan PromotionDeadline(TimingBudget budget) => 2 * budget.SafetyTick;

    public static Decision Decide(ElectionState? state, AgObservation obs, TimingBudget budget)
    {
        var replicas = obs.Replicas.OrderBy(r => r.Name, StringComparer.Ordinal).ToList(); // I9: no dependence on order
        obs = obs with { Replicas = replicas };
        return state switch
        {
            null or { Stage: ElectionStage.Done or ElectionStage.Aborted } => Steady(obs, budget),
            { Stage: ElectionStage.Planned or ElectionStage.Isolating } => Isolating(state, obs, budget),
            { Stage: ElectionStage.Promoting } => Promoting(state, obs, budget),
            { Stage: ElectionStage.Routing } => Routing(state, obs),
            _ => Decision.Nothing(state, "unknown stage"),
        };
    }

    /// <summary>D: no election in progress — legitimacy, adoption, and whether one must start.</summary>
    private static Decision Steady(AgObservation obs, TimingBudget budget)
    {
        var primaries = obs.Replicas.Where(r => r.IsPrimary).ToList();
        var withQuorum = primaries.Where(p => p.SynchronizedSecondaries >= obs.EffectiveK).ToList();

        if (primaries.Count > 1)
        {
            // Two primaries: the one without its quorum goes offline now — its watchdog would in T_step.
            // The one the listener routes to is kept when none holds a quorum: right after an election
            // the new primary has not got its secondaries back yet, and a returning old primary made
            // both go offline.
            var keep = withQuorum.Count == 1 ? withQuorum[0].Name
                : withQuorum.Count == 0 ? primaries.FirstOrDefault(p => p.Name == obs.RoutedPrimary)?.Name
                : null;
            var commands = new List<Command>();
            foreach (var p in primaries.Where(p => p.Name != keep && p.SynchronizedSecondaries < obs.EffectiveK))
                commands.Add(new TakeOffline(p.Name, obs.Epoch, "a second primary without its quorum"));
            if (withQuorum.Count != 1)
                commands.Add(new RaiseNeedsAttention("TwoPrimaries",
                    $"{string.Join(", ", primaries.Select(p => p.Name))} answer as PRIMARY and {withQuorum.Count} hold a quorum: a human decides."));
            else if (obs.RoutedPrimary != withQuorum[0].Name)
                commands.Add(new Route(withQuorum[0].Name));
            return new(null, commands, "two primaries");
        }

        if (primaries.Count == 1)
        {
            var primary = primaries[0];
            // Never OFFLINE the only primary; one that lost its quorum is its watchdog's call.
            if (withQuorum.Count == 1 && obs.RoutedPrimary != primary.Name)
                return new(null, [new Route(primary.Name), new Report("PrimaryAdopted",
                    $"{primary.Name} is the primary and holds its quorum: the listener now routes to it.")], "adoption");
            return Decision.Nothing(null, withQuorum.Count == 1 ? "steady" : "primary without quorum: its watchdog decides");
        }

        return StartElection(obs, budget);
    }

    /// <summary>B.1: the preconditions of an election, on fresh observations.</summary>
    private static Decision StartElection(AgObservation obs, TimingBudget budget)
    {
        var from = obs.LastKnownPrimary ?? obs.RoutedPrimary;
        if (from is null)
            return Decision.Nothing(null, "no primary known: nothing to elect from");
        var old = obs.Replica(from);
        var secondaries = obs.Replicas.Where(r => r.Name != from && r.SynchronousCommit).ToList();
        var n = secondaries.Count;
        var isolatable = secondaries.Where(r => r.Reachable && r.Role == "SECONDARY").ToList();
        var id = $"fo-{obs.Epoch}-{from}";

        var vetoes = isolatable.Where(r => r.SeesPrimaryConnected).Select(r => r.Name).ToList();
        var vetoLifted = obs.Approvals?.Contains(id) == true;
        if (vetoes.Count > 0 && !vetoLifted)
            return Decision.Nothing(null, $"veto: {string.Join(", ", vetoes)} still see {from} connected (C13)");

        if (isolatable.Count + obs.EffectiveK <= n)
            return new(null, [new RaiseNeedsAttention("NoElectionQuorum",
                $"{isolatable.Count} secondaries reachable out of {n}, k={obs.EffectiveK}: V + k_eff must exceed N to promote without losing an acknowledged commit.")],
                "V + k_eff <= N");

        // A reachable PRIMARY never gets here: it is the only primary (Steady), and the operator never
        // takes the only primary offline — without its quorum, its own watchdog steps it down.
        var notServing = NotServing(old, isolatable, budget, vetoLifted);
        if (notServing is null)
            return Decision.Nothing(null, $"{from} may still serve: waiting for T_vote ({budget.Vote.TotalSeconds} s) on every secondary, or a proof");

        var state = new ElectionState(id, ElectionStage.Isolating, from, null, [], obs.Epoch, obs.Now);
        var commands = new List<Command> { new RecordIntent(state) };
        commands.AddRange(isolatable.Select(r => (Command)new Isolate(r.Name, obs.Epoch)));
        return new(state, commands, $"election started: {notServing}");
    }

    /// <summary>Why the old primary does not serve, or null.</summary>
    private static string? NotServing(ReplicaObservation? old, IReadOnlyList<ReplicaObservation> isolatable, TimingBudget budget, bool vetoLifted)
    {
        if (old is { Reachable: true, Offline: true })
            return "the old primary is OFFLINE";
        if (old is { DeathProof: true })
            return "the old primary is dead (container terminated, kubelet alive)";
        if (vetoLifted)
            return "veto lifted by a human; promotion waits T_vote after the isolation";
        if (old is { WatchdogAlive: false } || old is null)
            return null; // without its watchdog, T_vote proves nothing: a human decides (WatchdogUnavailable)
        return isolatable.All(r => r.PrimaryDisconnectedFor >= budget.Vote)
            ? $"every isolated secondary lost the primary for at least T_vote ({budget.Vote.TotalSeconds} s): its watchdog stepped it down"
            : null;
    }

    /// <summary>B.2 → B.3: isolation results, then the promotion of the most advanced replica.</summary>
    private static Decision Isolating(ElectionState state, AgObservation obs, TimingBudget budget)
    {
        var isolated = obs.Replicas.Where(r => r.Reachable && r.Role == "SECONDARY" && r.Suspended).Select(r => r.Name).ToList();
        var n = obs.Replicas.Count(r => r.Name != state.From && r.SynchronousCommit);
        var next = state with { Isolated = isolated, IsolatedAt = state.IsolatedAt ?? (isolated.Count > 0 ? obs.Now : null) };

        if (obs.Replica(state.From) is { IsPrimary: true, SynchronizedSecondaries: var synced } && synced >= obs.EffectiveK)
            return Abort(next, obs, $"{state.From} serves again with its quorum");

        if (isolated.Count + obs.EffectiveK <= n)
        {
            // The isolation is asked again of every reachable secondary not isolated yet (it was
            // issued once, at the start), and a stalled one is a human's call.
            var commands = new List<Command> { new RecordIntent(next) };
            commands.AddRange(obs.Replicas
                .Where(r => r.Name != state.From && r.SynchronousCommit && r.Reachable && r.Role == "SECONDARY" && !r.Suspended)
                .Select(r => (Command)new Isolate(r.Name, obs.Epoch)));
            if (obs.Now - state.StartedAt >= IsolationDeadline(budget))
                commands.Add(new RaiseNeedsAttention("ElectionStalled",
                    $"The election of {obs.Ag} started at {state.StartedAt:O} has isolated {isolated.Count} of {n} secondaries: " +
                    "V + k_eff must exceed N to promote. Bring the secondaries back, or let the old primary serve again."));
            return new(next, commands, $"isolating: {isolated.Count} of {n} isolated");
        }

        if (obs.Approvals?.Contains(state.Id) == true && next.IsolatedAt is { } at && obs.Now - at < budget.Vote)
            return new(next, [new RecordIntent(next)], "veto lifted: waiting T_vote after the isolation");

        var target = MostAdvanced(obs, isolated);
        if (target is null)
            return new(next, [new RecordIntent(next), new RaiseNeedsAttention("NoMostAdvancedReplica",
                "No isolated replica is the most advanced on every database: a human chooses the target.")], "no single most advanced replica");

        var promoting = next with { Stage = ElectionStage.Promoting, To = target, PromotingSince = obs.Now };
        return new(promoting, [new RecordIntent(promoting), new Promote(target, obs.Epoch, AllowDataLoss: true)], $"promote {target}");
    }

    /// <summary>The isolated replica whose hardened LSN is the highest on every database, or null.</summary>
    public static string? MostAdvanced(AgObservation obs, IReadOnlyCollection<string> candidates)
    {
        var replicas = obs.Replicas.Where(r => candidates.Contains(r.Name)).ToList();
        if (replicas.Count == 0)
            return null;
        foreach (var r in replicas)
        {
            var best = obs.Databases.All(db => replicas.All(o => Lsn(r, db) >= Lsn(o, db)));
            if (best)
                return r.Name;
        }
        return null;
    }

    private static decimal Lsn(ReplicaObservation r, string db) => r.HardenedLsn?.GetValueOrDefault(db) ?? 0m;

    private static Decision Promoting(ElectionState state, AgObservation obs, TimingBudget budget)
    {
        if (obs.Replica(state.To!) is { IsPrimary: true })
        {
            var routing = state with { Stage = ElectionStage.Routing };
            return new(routing, [new RecordIntent(routing), new Route(state.To!), new ConfirmRole(state.To!)], $"{state.To} is primary: route");
        }
        // Not confirmed past the deadline: a human decides, the forced promotion is no longer re-issued.
        var since = state.PromotingSince ?? state.IsolatedAt ?? state.StartedAt;
        if (obs.Now - since >= PromotionDeadline(budget))
            return new(state, [new RaiseNeedsAttention("PromotionNotConfirmed",
                $"{state.To} was promoted at {since:O} and does not answer as PRIMARY: a human decides (the isolated " +
                $"secondaries {string.Join(", ", state.Isolated)} stay suspended).")], $"{state.To} not confirmed: a human decides");
        // Not confirmed yet: promote again (idempotent) under the same epoch.
        return new(state, [new Promote(state.To!, obs.Epoch, AllowDataLoss: true)], $"waiting for {state.To} to be primary");
    }

    private static Decision Routing(ElectionState state, AgObservation obs)
    {
        if (obs.RoutedPrimary != state.To)
            return new(state, [new Route(state.To!)], "routing");
        var done = state with { Stage = ElectionStage.Done };
        var commands = new List<Command> { new ClearIntent(state.Id) };
        commands.AddRange(state.Isolated.Where(r => r != state.To).Select(r => (Command)new Resume(r, obs.Epoch)));
        commands.Add(new Report("ElectionCompleted", $"{state.To} replaces {state.From} as primary of {obs.Ag}."));
        return new(done, commands, "done");
    }

    /// <summary>B.6: before the promotion, the isolated replicas resume and the old primary keeps its quorum.</summary>
    private static Decision Abort(ElectionState state, AgObservation obs, string why)
    {
        var aborted = state with { Stage = ElectionStage.Aborted };
        var commands = new List<Command> { new ClearIntent(state.Id) };
        commands.AddRange(state.Isolated.Select(r => (Command)new Resume(r, obs.Epoch)));
        commands.Add(new Report("ElectionAborted", why));
        return new(aborted, commands, $"aborted: {why}");
    }

    /// <summary>C: a planned switchover — the primary is demoted first, then the most advanced
    /// synchronized secondary promoted; no isolation needed.</summary>
    public static IReadOnlyList<Command> PlanSwitchover(AgObservation obs, string from, string to)
        => [new Demote(from, obs.Epoch), new Promote(to, obs.Epoch, AllowDataLoss: false), new Route(to), new ConfirmRole(to)];
}
