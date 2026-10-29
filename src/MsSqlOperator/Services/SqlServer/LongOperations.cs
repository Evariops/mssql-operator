using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>The state of a long operation: still running, or done with its result or its error.</summary>
public sealed record LongOperationState(bool Running, object? Result, Exception? Error, DateTimeOffset StartedAt);

/// <summary>
/// Operations that last longer than a reconcile pass — the initialization backup of a
/// database, hours for a large one — run here, in a bounded pool, and the pass only starts them and
/// follows them. Run inside the pass, such a backup held a worker of its kind for its whole length,
/// and was cut by the reconcile timeout. The operations live in this process: one lost with it is
/// started again by the next pass, after checking that SQL Server no longer runs it.
/// </summary>
public sealed class LongOperations(ILogger<LongOperations>? logger = null, int maxConcurrent = 4, TimeSpan? shortWait = null)
{
    private readonly SemaphoreSlim _pool = new(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));
    private readonly TimeSpan _shortWait = shortWait ?? TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Entry> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    private sealed class Entry(Task<object?> task, DateTimeOffset startedAt)
    {
        public Task<object?> Task { get; } = task;
        public DateTimeOffset StartedAt { get; } = startedAt;
    }

    /// <summary>Starts <paramref name="work"/> under <paramref name="key"/> unless one already runs or
    /// waits there; false then.</summary>
    public bool TryStart<T>(string key, Func<CancellationToken, Task<T>> work)
    {
        var started = false;
        _operations.GetOrAdd(key, _ =>
        {
            started = true;
            return new Entry(RunAsync(key, work), DateTimeOffset.UtcNow);
        });
        return started;
    }

    private async Task<object?> RunAsync<T>(string key, Func<CancellationToken, Task<T>> work)
    {
        await Task.Yield();
        await _pool.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            logger?.LogInformation("Long operation {Key} started", key);
            var result = await work(_stopping.Token).ConfigureAwait(false);
            logger?.LogInformation("Long operation {Key} done", key);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Long operation {Key} failed", key);
            throw;
        }
        finally
        {
            _pool.Release();
        }
    }

    /// <summary>The state of the operation under <paramref name="key"/>; null when none was started.</summary>
    public LongOperationState? StateOf(string key)
    {
        if (!_operations.TryGetValue(key, out var entry))
            return null;
        var task = entry.Task;
        if (!task.IsCompleted)
            return new LongOperationState(true, null, null, entry.StartedAt);
        return task.IsCompletedSuccessfully
            ? new LongOperationState(false, task.Result, null, entry.StartedAt)
            : new LongOperationState(false, null, task.Exception?.GetBaseException() ?? new OperationCanceledException(), entry.StartedAt);
    }

    /// <summary>The state of the operation under <paramref name="key"/> once it is done, or after a few
    /// seconds while it still runs: the pass that starts a short one (the initialization backup of a
    /// small database, a second) follows it up at once instead of on its next requeue.</summary>
    public async Task<LongOperationState?> WaitAsync(string key, CancellationToken ct = default)
    {
        if (_operations.TryGetValue(key, out var entry) && !entry.Task.IsCompleted)
            await Task.WhenAny(entry.Task, Task.Delay(_shortWait, ct)).ConfigureAwait(false);
        return StateOf(key);
    }

    /// <summary>Forgets a finished operation once its result was taken.</summary>
    public void Forget(string key) => _operations.TryRemove(key, out _);

    /// <summary>Cancels what runs (process stopping).</summary>
    public void Stop() => _stopping.Cancel();
}
