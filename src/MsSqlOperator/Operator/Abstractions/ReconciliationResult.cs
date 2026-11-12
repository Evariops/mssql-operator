namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Outcome of a reconcile/finalize pass, replacing KubeOps' <c>ReconciliationResult</c>.
/// <see cref="RequeueAfter"/> asks the runtime to re-enqueue the entity after the delay.
/// </summary>
public sealed class ReconciliationResult<TEntity>
{
    public TEntity Entity { get; init; } = default!;

    public TimeSpan? RequeueAfter { get; init; }

    /// <summary>The requeue goes ahead of the ordinary keys of its kind: an AG without a primary or
    /// in the middle of a failover is not served behind every other AG of the fleet.</summary>
    public bool Urgent { get; init; }

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public Exception? Error { get; init; }

    public static ReconciliationResult<TEntity> Success(TEntity entity, TimeSpan? requeueAfter = null)
        => new() { Entity = entity, RequeueAfter = requeueAfter, IsSuccess = true };

    public static ReconciliationResult<TEntity> Failure(TEntity entity, string message, Exception? error = null, TimeSpan? requeueAfter = null)
        => new() { Entity = entity, IsSuccess = false, ErrorMessage = message, Error = error, RequeueAfter = requeueAfter };
}
