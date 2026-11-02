namespace MsSqlOperator.Core.Maintenance;

/// <summary>An eviction request as the webhook records it on the pod: when it was first seen, who
/// asked (the user of the admission request), and when it was last asked again. Drain tools retry
/// every 5 seconds; a request no longer repeated is abandoned (<see cref="EvictionRequests.AbandonedAfter"/>).</summary>
public sealed record EvictionRecord(DateTimeOffset RequestedAt, string? RequestedBy, DateTimeOffset LastRequestAt)
{
    /// <summary>A node asked: the internal drain of <c>talosctl upgrade</c>, under the kubelet identity.</summary>
    public bool FromNode => EvictionRequests.IsNode(RequestedBy);

    public EvictionRequest ToRequest(string pod) => new(pod, RequestedAt, FromNode);
}

/// <summary>A verdict as the leader publishes it on the pod: valid until <see cref="ValidUntil"/>, after
/// which the webhook no longer trusts it.</summary>
public sealed record PublishedVerdict(VerdictDecision Decision, string Reason, DateTimeOffset Deadline, DateTimeOffset ValidUntil);

/// <summary>What the webhook answers: allowed, or refused with a 429 and a message the drain tool
/// logs. <see cref="DeadlineReached"/> says that the eviction goes on because its delay is spent.</summary>
public sealed record EvictionAnswer(bool Allowed, string Message, bool DeadlineReached = false);

/// <summary>
/// The eviction webhook, as pure functions of the request, of what the pod carries and of
/// the time. Every path that cannot decide allows the eviction (principle 1): a request that could not
/// be recorded, a deadline reached, a manual release.
/// </summary>
public static class EvictionRequests
{
    /// <summary>A request not repeated for this long is abandoned: the drain stopped. The next one
    /// starts a new delay.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromSeconds(120);

    /// <summary>How stale the last-request time may get before a repeated request rewrites it: one
    /// pod patch every 30 s at most while a drain retries.</summary>
    public static readonly TimeSpan LastRequestRefresh = TimeSpan.FromSeconds(30);

    /// <summary>How long a published verdict stays valid. The leader refreshes it on every pass while
    /// the request stands; a verdict not refreshed (leader gone) stops counting after this.</summary>
    public static readonly TimeSpan VerdictValidity = TimeSpan.FromSeconds(30);

    public static bool IsNode(string? user) => user?.StartsWith("system:node:", StringComparison.Ordinal) == true;

    public static bool IsAbandoned(EvictionRecord record, DateTimeOffset now) => now - record.LastRequestAt >= AbandonedAfter;

    /// <summary>The record after a request from <paramref name="requester"/> at <paramref name="now"/>,
    /// or null when the current one stands as it is (nothing to write). A node asking after another
    /// requester takes over the request: its shorter deadline applies from the first request.</summary>
    public static EvictionRecord? Next(EvictionRecord? current, string? requester, DateTimeOffset now)
    {
        if (current is null || IsAbandoned(current, now))
            return new(now, requester, now);
        var nodeTakesOver = IsNode(requester) && !current.FromNode;
        if (!nodeTakesOver && now - current.LastRequestAt < LastRequestRefresh)
            return null;
        return current with { RequestedBy = nodeTakesOver ? requester : current.RequestedBy, LastRequestAt = now };
    }

    /// <summary>
    /// The answer to an eviction in <c>Enforce</c>, from the request recorded on the pod (null when it
    /// could not be recorded) and the verdict published by the leader:
    /// <list type="bullet">
    /// <item>no record, a manual release, or the deadline reached: allow;</item>
    /// <item>a valid verdict: its decision;</item>
    /// <item>no verdict, or a stale one: delay, until the deadline at the latest.</item>
    /// </list>
    /// </summary>
    public static EvictionAnswer Answer(string resource, EvictionRecord? record, PublishedVerdict? verdict, bool released,
        DateTimeOffset now, EvictionBudget budget)
    {
        if (record is null)
            return new(true, $"mssql-operator, {resource}: the eviction request could not be recorded; the eviction goes on.");
        if (released)
            return new(true, $"mssql-operator, {resource}: released by the maintenance-release annotation.");

        var deadline = budget.DeadlineOf(record.ToRequest(string.Empty));
        if (now >= deadline)
            return new(true, $"mssql-operator, {resource}: delay spent ({Seconds(deadline - record.RequestedAt)} s); the eviction goes on.",
                DeadlineReached: true);

        var progress = $"delayed {Seconds(now - record.RequestedAt)} s of {Seconds(deadline - record.RequestedAt)} s at most";
        if (verdict is not null && now < verdict.ValidUntil)
            return verdict.Decision == VerdictDecision.Allow
                ? new(true, $"mssql-operator, {resource}: {verdict.Reason}.")
                : new(false, $"mssql-operator, {resource}: {verdict.Reason}; {progress}.");
        return new(false, $"mssql-operator, {resource}: waiting for the operator's verdict; {progress}.");
    }

    private static long Seconds(TimeSpan span) => (long)Math.Max(0, span.TotalSeconds);
}
