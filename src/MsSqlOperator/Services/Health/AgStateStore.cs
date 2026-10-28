using System.Collections.Concurrent;
using MsSqlOperator.Core.State;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// The latest state read of each AG, in memory — fed by every health read (the AG
/// controller now, the supervisor at level 3). The other controllers find the primary here instead of
/// connecting to every replica in turn; an entry older than <see cref="AgStateStore.FreshFor"/> is not trusted, and
/// they fall back to scanning. Updating returns the transitions since the previous read.
/// </summary>
public interface IAgStateStore
{
    IReadOnlyList<Transition> Update(string ns, string ag, AgSnapshot? snapshot);
    AgSnapshot? Get(string ns, string ag);
    /// <summary>The primary of the last read, when it is fresh enough to be a hint.</summary>
    string? FreshPrimary(string ns, string ag);
    void Forget(string ns, string ag);
}

public sealed class AgStateStore(TimeProvider? clock = null) : IAgStateStore
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string, string), AgSnapshot?> _snapshots = new();
    private TimeProvider Clock => clock ?? TimeProvider.System;

    public IReadOnlyList<Transition> Update(string ns, string ag, AgSnapshot? snapshot)
    {
        _snapshots.TryGetValue((ns, ag), out var previous);
        _snapshots[(ns, ag)] = snapshot;
        return AgTransitions.Diff(previous, snapshot);
    }

    public AgSnapshot? Get(string ns, string ag) => _snapshots.TryGetValue((ns, ag), out var s) ? s : null;

    public string? FreshPrimary(string ns, string ag)
        => Get(ns, ag) is { Primary: { } primary } snapshot && Clock.GetUtcNow() - snapshot.TakenAt <= FreshFor ? primary : null;

    public void Forget(string ns, string ag) => _snapshots.TryRemove((ns, ag), out _);

    /// <summary>The snapshot of a health report.</summary>
    public static AgSnapshot FromHealth(string ag, AgHealthReport health, DateTimeOffset at)
        // Two primaries are no hint: the callers must not run DDL against an arbitrary one.
        => new(ag, at, health.DetectedPrimaries is { Count: > 1 } ? null : health.PrimaryReplica, health.RequiredSynchronizedSecondaries ?? 0,
            health.ReplicaStates.Select(r => new ReplicaState(r.Name, r.Role, r.ConnectionState == "CONNECTED", r.SynchronizationHealth, r.AvailabilityMode)).ToList(),
            health.DatabaseStates.Select(d => new DatabaseState(d.DatabaseName, d.ReplicaName, d.SynchronizationState, d.IsSuspended)).ToList());
}
