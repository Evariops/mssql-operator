using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// An existing AG moves to Quorum by bringing the availability modes to what the profile
/// needs — the replicas the topology designates synchronous made SYNCHRONOUS_COMMIT, one at a time,
/// each one synchronized before the next. Until then the AG keeps the floor of Legacy: raising k to
/// 1 with no synchronous secondary healthy would block every write.
/// </summary>
public static class DurabilityConvergence
{
    /// <param name="designatedSync">The replicas the topology wants in synchronous commit.</param>
    /// <param name="states">The replicas as the health read saw them.</param>
    /// <returns>The replica to raise now (null: wait, or nothing to do), and whether the modes have
    /// converged — every designated replica synchronous, every such secondary healthy.</returns>
    public static (string? Next, bool Converged) Plan(IReadOnlyCollection<string> designatedSync, IReadOnlyList<AgReplicaStateInfo> states)
    {
        var designated = states.Where(s => designatedSync.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        bool IsSync(AgReplicaStateInfo s) => s.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase);
        bool IsSecondary(AgReplicaStateInfo s) => !s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
        bool Healthy(AgReplicaStateInfo s) => s.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)
                                              && s.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);

        // One at a time: a secondary raised earlier is waited for until it is synchronized.
        if (designated.Any(s => IsSecondary(s) && IsSync(s) && !Healthy(s)))
            return (null, false);
        var pending = designated.Where(s => !IsSync(s) && (!IsSecondary(s) || s.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => IsSecondary(s) ? 0 : 1).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        if (pending.Count > 0)
            return (pending[0].Name, false);
        var converged = designated.Count == designatedSync.Count && designated.All(s => IsSync(s) && (!IsSecondary(s) || Healthy(s)));
        return (null, converged);
    }

    /// <summary>Every designated replica is in synchronous commit — the modes Quorum
    /// needs are there, whatever the health of the replicas. A partition makes the secondaries
    /// unhealthy, not asynchronous: it is no convergence to wait for.</summary>
    public static bool ModesReached(IReadOnlyCollection<string> designatedSync, IReadOnlyList<AgReplicaStateInfo> states)
    {
        var designated = states.Where(s => designatedSync.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return designated.Count == designatedSync.Count
               && designated.All(s => s.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase));
    }
}
