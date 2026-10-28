namespace MsSqlOperator.Core.State;

/// <summary>One replica as a state read saw it.</summary>
public sealed record ReplicaState(string Name, string Role, bool Connected, string SynchronizationHealth, string AvailabilityMode);

/// <summary>One database on one replica.</summary>
public sealed record DatabaseState(string Database, string Replica, string SynchronizationState, bool Suspended);

/// <summary>
/// What one read-only state read of an AG saw — on its primary, whose sys.dm_hadr_* views
/// describe the whole group. Two consecutive snapshots give the transitions.
/// </summary>
public sealed record AgSnapshot(string Ag, DateTimeOffset TakenAt, string? Primary, int RequiredSyncSecondaries,
    IReadOnlyList<ReplicaState> Replicas, IReadOnlyList<DatabaseState> Databases);

public enum TransitionKind
{
    ReadFailed,
    ReadRecovered,
    PrimaryChanged,
    RoleChanged,
    ReplicaDisconnected,
    ReplicaConnected,
    ReplicaAppeared,
    ReplicaDisappeared,
    SynchronizationHealthChanged,
    DatabaseSuspended,
    DatabaseResumed,
    DatabaseSynchronizationChanged,
}

/// <summary>A change between two state reads; <see cref="Subject"/> is the replica, or
/// <c>database@replica</c>.</summary>
public sealed record Transition(TransitionKind Kind, string Subject, string? From, string? To);

/// <summary>
/// The transitions between two snapshots — only what changed is propagated; nothing that
/// did not change wakes anybody. A failed read is itself a signal (ReadFailed).
/// Pure: the same two snapshots always give the same transitions, in the same order (I9).
/// </summary>
public static class AgTransitions
{
    public static IReadOnlyList<Transition> Diff(AgSnapshot? previous, AgSnapshot? current)
    {
        var transitions = new List<Transition>();
        if (current is null)
        {
            if (previous is not null)
                transitions.Add(new(TransitionKind.ReadFailed, previous.Ag, previous.Primary, null));
            return transitions;
        }
        if (previous is null)
            return [new(TransitionKind.ReadRecovered, current.Ag, null, current.Primary)];

        if (previous.Primary != current.Primary)
            transitions.Add(new(TransitionKind.PrimaryChanged, current.Ag, previous.Primary, current.Primary));

        var before = previous.Replicas.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var after = current.Replicas.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(name, out var b);
            after.TryGetValue(name, out var a);
            if (b is null) { transitions.Add(new(TransitionKind.ReplicaAppeared, name, null, a!.Role)); continue; }
            if (a is null) { transitions.Add(new(TransitionKind.ReplicaDisappeared, name, b.Role, null)); continue; }
            if (b.Role != a.Role)
                transitions.Add(new(TransitionKind.RoleChanged, name, b.Role, a.Role));
            if (b.Connected != a.Connected)
                transitions.Add(new(a.Connected ? TransitionKind.ReplicaConnected : TransitionKind.ReplicaDisconnected, name, null, null));
            if (b.SynchronizationHealth != a.SynchronizationHealth)
                transitions.Add(new(TransitionKind.SynchronizationHealthChanged, name, b.SynchronizationHealth, a.SynchronizationHealth));
        }

        var dbBefore = previous.Databases.ToDictionary(d => $"{d.Database}@{d.Replica}", StringComparer.Ordinal);
        foreach (var d in current.Databases.OrderBy(d => $"{d.Database}@{d.Replica}", StringComparer.Ordinal))
        {
            var key = $"{d.Database}@{d.Replica}";
            if (!dbBefore.TryGetValue(key, out var b))
                continue;
            if (b.Suspended != d.Suspended)
                transitions.Add(new(d.Suspended ? TransitionKind.DatabaseSuspended : TransitionKind.DatabaseResumed, key, null, null));
            if (b.SynchronizationState != d.SynchronizationState)
                transitions.Add(new(TransitionKind.DatabaseSynchronizationChanged, key, b.SynchronizationState, d.SynchronizationState));
        }
        return transitions;
    }
}
