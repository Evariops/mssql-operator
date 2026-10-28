namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Non-generic handle to a per-entity-type reconciliation runtime (watch + workers),
/// driven by the hosted service / leader election.
/// </summary>
public interface IEntityRuntime
{
    string EntityKind { get; }

    /// <summary>Runs the watch + worker loops until <paramref name="ct"/> is cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>
    /// Enqueues a key (<c>{ns}/{name}</c>) for reconciliation from outside the CR watch. Used by
    /// the owned-resource watch to route a child-resource event to its parent runtime; the
    /// work queue dedups, so repeated enqueues of the same parent collapse to a single reconcile.
    /// </summary>
    void Enqueue(string key);

    /// <summary>The key (<c>{ns}/{name}</c>) of the resource with this UID, among those the
    /// runtime has seen; null when unknown. Key-material Secrets and PVCs name their owner by UID only.</summary>
    string? KeyOfUid(string uid);
}
