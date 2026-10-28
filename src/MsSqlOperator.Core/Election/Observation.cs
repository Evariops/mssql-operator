namespace MsSqlOperator.Core.Election;

/// <summary>
/// What the operator read of one replica during the current re-evaluation (a fresh
/// observation, never a cache). Durations are measured by the replica on its own clock.
/// </summary>
/// <param name="Name">The pod name of the replica.</param>
/// <param name="Reachable">The operator read it during this re-evaluation.</param>
/// <param name="Role">PRIMARY, SECONDARY, RESOLVING; null when unreachable.</param>
/// <param name="Offline">The AG is OFFLINE on this replica.</param>
/// <param name="SynchronousCommit">The replica is in SYNCHRONOUS_COMMIT mode.</param>
/// <param name="SeesPrimaryConnected">For a secondary: its connection to the primary is up.</param>
/// <param name="PrimaryDisconnectedFor">For a secondary: how long it has lost the primary, by its own clock (watchdog table).</param>
/// <param name="SynchronizedSecondaries">For a primary: the secondaries it sees SYNCHRONIZED — its quorum.</param>
/// <param name="Suspended">Data movement is suspended on every database of the AG (isolated).</param>
/// <param name="HardenedLsn">Per database, the last hardened LSN.</param>
/// <param name="WatchdogAlive">Its watchdog job ran within the last 3 periods.</param>
/// <param name="DeathProof">For the old primary: its SQL container is terminated in the pod status, reported by a kubelet whose node Lease was renewed after it.</param>
public sealed record ReplicaObservation(
    string Name,
    bool Reachable,
    string? Role = null,
    bool Offline = false,
    bool SynchronousCommit = true,
    bool SeesPrimaryConnected = false,
    TimeSpan? PrimaryDisconnectedFor = null,
    int SynchronizedSecondaries = 0,
    bool Suspended = false,
    IReadOnlyDictionary<string, decimal>? HardenedLsn = null,
    bool WatchdogAlive = true,
    bool DeathProof = false)
{
    public bool IsPrimary => Reachable && Role == "PRIMARY" && !Offline;
}

/// <summary>An AG as read during one re-evaluation.</summary>
/// <param name="Ag">The AG name.</param>
/// <param name="Epoch">The operator's current epoch.</param>
/// <param name="RequiredSyncSecondaries">k: REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT.</param>
/// <param name="Replicas">Every replica of the spec.</param>
/// <param name="RoutedPrimary">The replica the listener Service selects by name.</param>
/// <param name="LastKnownPrimary">The primary of the last state read, when the current one has none.</param>
/// <param name="Now">The time of this re-evaluation, as the shell's clock gives it.</param>
/// <param name="Approvals">Approval ids a human gave (veto lifted, data loss accepted).</param>
public sealed record AgObservation(
    string Ag,
    long Epoch,
    int RequiredSyncSecondaries,
    IReadOnlyList<ReplicaObservation> Replicas,
    string? RoutedPrimary,
    string? LastKnownPrimary,
    DateTimeOffset Now,
    IReadOnlyCollection<string>? Approvals = null)
{
    /// <summary>k_eff = max(k, 1).</summary>
    public int EffectiveK => Math.Max(RequiredSyncSecondaries, 1);

    public ReplicaObservation? Replica(string name) => Replicas.FirstOrDefault(r => r.Name == name);

    public IReadOnlyCollection<string> Databases
        => Replicas.SelectMany(r => r.HardenedLsn?.Keys ?? Enumerable.Empty<string>()).Distinct().Order(StringComparer.Ordinal).ToList();
}
