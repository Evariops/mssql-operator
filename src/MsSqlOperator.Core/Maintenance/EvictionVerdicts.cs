namespace MsSqlOperator.Core.Maintenance;

public enum VerdictDecision { Allow, Delay }

/// <summary>What the eviction webhook answers for a pod: allow, or delay (429) with a reason a human
/// reads in the drain logs, until <see cref="Deadline"/> at the latest. <see cref="FailoverTarget"/>:
/// the planned failover to run now, before the primary leaves.</summary>
public sealed record Verdict(VerdictDecision Decision, string Reason, DateTimeOffset Deadline, string? FailoverTarget = null);

/// <summary>A replica of the AG, from a fresh observation: its pod, its role, and whether it can take
/// over without loss (SYNCHRONIZED on every database, in synchronous commit, not seeding). The
/// primary counts as synchronized. <see cref="RedoSeconds"/>: the log it still has to redo, in
/// seconds at its redo rate, on its slowest database.</summary>
public sealed record ReplicaView(
    string Pod,
    string? Node,
    bool Primary,
    bool Ready,
    bool Synchronized,
    bool SynchronousCommit,
    bool Seeding,
    double RedoSeconds = 0);

/// <summary>The target of a planned failover of the primary: none (no replica can take over
/// without loss), not yet (its redo is behind), or now.</summary>
public sealed record FailoverChoice(string? Target, bool Now, string Reason);

/// <summary>An eviction the webhook recorded on a pod: when, and whether a node asked for it (the
/// internal drain of <c>talosctl upgrade</c>, which gives up after 5 minutes).</summary>
public sealed record EvictionRequest(string Pod, DateTimeOffset RequestedAt, bool FromNode);

/// <summary>How long an eviction may be delayed, by requester.</summary>
public sealed record EvictionBudget(TimeSpan NodeRequester, TimeSpan OtherRequester)
{
    /// <summary>Talos abandons its drain after 5 minutes and forces a 60-second grace period: 210 s
    /// leave the orderly shutdown its time inside that window. The other drain tools wait for ever
    /// by default; 600 s cover a long resynchronization.</summary>
    public static readonly EvictionBudget Default = new(TimeSpan.FromSeconds(210), TimeSpan.FromSeconds(600));

    public DateTimeOffset DeadlineOf(EvictionRequest request)
        => request.RequestedAt + (request.FromNode ? NodeRequester : OtherRequester);
}

/// <summary>
/// The verdict on the eviction of a replica, as a pure function of a fresh observation of
/// the AG and of the signals of its pods. It never blocks: past the deadline, the answer is allow
/// (principle 1). Before it, a replica leaves only when its departure costs neither a copy nor a
/// lossless failover target:
/// <list type="bullet">
/// <item>the primary waits for a planned failover, or for the deadline when no replica can take
/// over without loss. Without a planned failover to wait for (<c>plannedFailover</c> false: the
/// proactive failover is not there, or the actuation gateway holds it back), the primary waits
/// like a secondary, then leaves, and the failover is the unplanned one;</item>
/// <item>a secondary waits while another replica is missing, not ready or not synchronized, while a
/// disruptive operation runs on the AG, or while another replica whose eviction was
/// requested earlier has not left yet — the oldest request goes first, so that two drains never hold
/// each other until their deadlines and then leave together;</item>
/// <item>when every replica is on a node that is about to stop, the AG stops anyway: nothing is
/// gained by waiting, and the eviction is allowed at once.</item>
/// </list>
/// </summary>
public static class EvictionVerdicts
{
    /// <summary>The verdict on <paramref name="request"/>; <paramref name="requests"/> holds every eviction
    /// recorded on a pod of the AG, this one included. <paramref name="plannedFailover"/>: the operator
    /// fails the primary over before it leaves.</summary>
    public static Verdict Decide(
        EvictionRequest request,
        IReadOnlyList<ReplicaView> replicas,
        IReadOnlyDictionary<string, IReadOnlyList<MaintenanceSignal>> signalsByPod,
        IReadOnlyCollection<EvictionRequest> requests,
        string? operationInProgress,
        DateTimeOffset now,
        EvictionBudget budget,
        bool plannedFailover = true,
        IReadOnlyList<string>? preferredTargets = null)
    {
        var deadline = budget.DeadlineOf(request);
        if (now >= deadline)
            return new(VerdictDecision.Allow, "deadline reached", deadline);

        var self = replicas.FirstOrDefault(r => r.Pod == request.Pod);
        if (self is null)
            return new(VerdictDecision.Allow, "not a replica of the availability group", deadline);

        var others = replicas.Where(r => r.Pod != request.Pod).ToList();
        if (others.Count == 0 || others.All(r => NodeStops(r, signalsByPod)))
            return new(VerdictDecision.Allow, "every other replica is on a node that is about to stop", deadline);

        if (self.Primary && plannedFailover)
        {
            var choice = FailoverTargetFor(self.Pod, replicas, signalsByPod, preferredTargets);
            return new(VerdictDecision.Delay, choice.Reason, deadline, choice.Now ? choice.Target : null);
        }

        if (operationInProgress is not null)
            return new(VerdictDecision.Delay, $"{operationInProgress} in progress on the availability group", deadline);

        if (others.FirstOrDefault(r => !r.Ready || (!r.Synchronized && r.SynchronousCommit) || r.Seeding) is { } waited)
            return new(VerdictDecision.Delay, $"waiting for {waited.Pod} to be {(waited.Ready ? "synchronized" : "back")}", deadline);

        if (others.FirstOrDefault(r => Stopping(r, signalsByPod) || RequestedBefore(r, request, requests)) is { } first)
            return new(VerdictDecision.Delay, $"{first.Pod} leaves first", deadline);

        return self.Primary
            ? new(VerdictDecision.Allow, "the other replicas are synchronized; no planned failover: the primary leaves and the failover is unplanned", deadline)
            : new(VerdictDecision.Allow, "the other replicas are synchronized", deadline);
    }

    /// <summary>A target whose redo is further behind than this makes the failover wait, with the
    /// primary still serving: after the promotion, the databases take writes once the redo is replayed.</summary>
    public static readonly double MaxRedoSeconds = 5;

    /// <summary>
    /// The target of a planned failover of <paramref name="primary"/>. A candidate is
    /// ready, synchronized, in synchronous commit, not seeding, and has no signal of its own (a
    /// replica of a node that only sees an eviction of another pod stays a candidate). The smallest
    /// redo wins (to the second), then the order of <paramref name="preferredTargets"/>, then the
    /// pod name. The failover waits while the redo of the best one exceeds <see cref="MaxRedoSeconds"/>.
    /// </summary>
    public static FailoverChoice FailoverTargetFor(string primary, IReadOnlyList<ReplicaView> replicas,
        IReadOnlyDictionary<string, IReadOnlyList<MaintenanceSignal>> signalsByPod, IReadOnlyList<string>? preferredTargets = null)
    {
        var preferred = preferredTargets ?? [];
        int Rank(string pod)
        {
            for (var i = 0; i < preferred.Count; i++)
                if (preferred[i] == pod)
                    return i;
            return int.MaxValue;
        }
        var best = replicas
            .Where(r => r.Pod != primary && r.Ready && r.Synchronized && r.SynchronousCommit && !r.Seeding && !Leaving(r, signalsByPod))
            .OrderBy(r => Math.Ceiling(Math.Max(0, r.RedoSeconds)))
            .ThenBy(r => Rank(r.Pod))
            .ThenBy(r => r.Pod, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
            return new(null, false, "no replica can take over without loss: waiting for one, until the deadline");
        if (best.RedoSeconds > MaxRedoSeconds)
            return new(best.Pod, false, $"waiting for the redo of {best.Pod} to catch up ({Math.Ceiling(best.RedoSeconds):0} s) before the planned failover");
        return new(best.Pod, true, $"planned failover to {best.Pod} before the primary leaves");
    }

    /// <summary>The replica is already stopping: its pod is deleted or terminated by the kubelet.</summary>
    private static bool Stopping(ReplicaView replica, IReadOnlyDictionary<string, IReadOnlyList<MaintenanceSignal>> signalsByPod)
        => signalsByPod.TryGetValue(replica.Pod, out var signals)
           && signals.Any(s => s.Kind is SignalKind.Deletion or SignalKind.KubeletTermination);

    /// <summary>The eviction of the replica was requested before this one (ties broken by pod name).</summary>
    private static bool RequestedBefore(ReplicaView replica, EvictionRequest request, IReadOnlyCollection<EvictionRequest> requests)
        => requests.FirstOrDefault(r => r.Pod == replica.Pod) is { } other
           && (other.RequestedAt < request.RequestedAt
               || (other.RequestedAt == request.RequestedAt && string.CompareOrdinal(other.Pod, request.Pod) < 0));

    /// <summary>The replica has a signal of its own: it is about to stop, and is no failover target.</summary>
    private static bool Leaving(ReplicaView replica, IReadOnlyDictionary<string, IReadOnlyList<MaintenanceSignal>> signalsByPod)
        => signalsByPod.TryGetValue(replica.Pod, out var signals) && signals.Count > 0;

    /// <summary>The node of the replica announces its stop (a node-scoped signal).</summary>
    private static bool NodeStops(ReplicaView replica, IReadOnlyDictionary<string, IReadOnlyList<MaintenanceSignal>> signalsByPod)
        => signalsByPod.TryGetValue(replica.Pod, out var signals) && signals.Any(s => s.Scope == SignalScope.Node);
}
