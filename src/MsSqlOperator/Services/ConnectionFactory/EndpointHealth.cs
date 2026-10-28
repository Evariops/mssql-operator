using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Safety;

namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>A connection refused because its endpoint was seen unavailable moments ago.</summary>
public sealed class EndpointUnavailableException(string host, string reason)
    : InvalidOperationException($"{host} was seen unavailable ({reason}): not waiting for a connection timeout");

/// <summary>
/// The health of each SQL endpoint, fed by the state reads (health check, supervisor). A
/// replica the last read could not reach fails a new connection at once instead of after the
/// connection timeouts and retries (about 2.6 min with the default profile) — a dead replica no longer
/// holds the workers of every kind that targets its AG. Its return, seen by a read or a pod event,
/// clears the mark at once. Behind the switch <c>TargetCircuitBreakers</c>: Off, Shadow (logged only,
/// the default) or Enforce.
/// </summary>
public sealed class EndpointHealth(SwitchMode mode = SwitchMode.Shadow, TimeProvider? clock = null, ILogger<EndpointHealth>? logger = null)
{
    public static readonly TimeSpan MarkFor = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Reason)> _down = new(StringComparer.OrdinalIgnoreCase);
    private TimeProvider Clock => clock ?? TimeProvider.System;

    public SwitchMode Mode { get; } = mode;

    public void MarkDown(string host, string reason) => _down[host] = (Clock.GetUtcNow(), reason);

    public void MarkUp(string host) => _down.TryRemove(host, out _);

    /// <summary>Throws <see cref="EndpointUnavailableException"/> (Enforce) when <paramref name="host"/>
    /// was marked down less than <see cref="MarkFor"/> ago; in Shadow, only says it would have.</summary>
    public void Check(string host)
    {
        if (Mode == SwitchMode.Off || !_down.TryGetValue(host, out var mark) || Clock.GetUtcNow() - mark.At > MarkFor)
            return;
        if (Mode == SwitchMode.Enforce)
            throw new EndpointUnavailableException(host, mark.Reason);
        logger?.LogDebug("Circuit breaker (Shadow): a connection to {Host} would fail fast ({Reason})", host, mark.Reason);
    }
}

/// <summary>Session settings applied to an already open connection.</summary>
public static class SqlSessions
{
    public static async Task ApplyAsync(Microsoft.Data.SqlClient.SqlConnection connection, string batch, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = batch;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// A concurrency limit per target, shared by every kind — at most
/// <see cref="PerTarget"/> DDL sessions at once on one SQL Server, whatever asks for them (users,
/// jobs, procedures, TDE). Replaces the per-feature static gates.
/// </summary>
public sealed class TargetLimiter
{
    public const int PerTarget = 2;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IAsyncDisposable> AcquireAsync(string target, CancellationToken ct, int slots = PerTarget)
    {
        var gate = _gates.GetOrAdd(target, _ => new SemaphoreSlim(slots, slots));
        await gate.WaitAsync(ct);
        return new Slot(gate);
    }

    /// <summary>Drops the gate of a deleted target.</summary>
    public void Forget(string target)
    {
        if (_gates.TryRemove(target, out var gate) && gate.CurrentCount == PerTarget)
            gate.Dispose();
    }

    private sealed class Slot(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
