namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Outcome of a reconcile/finalize pass, replacing KubeOps' <c>ReconciliationResult</c>.
/// <see cref="RequeueAfter"/> asks the runtime to re-enqueue the entity after the delay.
/// </summary>
public sealed class ReconciliationResult<TEntity>
{
    public TEntity Entity { get; init; } = default!;

    public TimeSpan? RequeueAfter { get; init; }

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public Exception? Error { get; init; }

    public static ReconciliationResult<TEntity> Success(TEntity entity, TimeSpan? requeueAfter = null)
        => new() { Entity = entity, RequeueAfter = requeueAfter, IsSuccess = true };

    public static ReconciliationResult<TEntity> Failure(TEntity entity, string message, Exception? error = null, TimeSpan? requeueAfter = null)
        => new() { Entity = entity, IsSuccess = false, ErrorMessage = message, Error = error, RequeueAfter = requeueAfter };
}
