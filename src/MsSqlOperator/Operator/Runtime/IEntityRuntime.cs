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
}
