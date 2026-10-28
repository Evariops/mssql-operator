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

/// <summary>
/// A concurrency limit per target (an instance or an AG), shared by every kind — at most
/// <see cref="PerTarget"/> passes at once work on one target, whatever kind they are (users, jobs,
/// configurations, TDE; the deployer has a slot of its own). Replaces the per-feature static gates.
/// Purged when the target is deleted.
/// </summary>
public sealed class TargetLimiter
{
    public const int PerTarget = 2;

    private readonly ConcurrentDictionary<string, (SemaphoreSlim Gate, int Slots)> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The key of a target: its kind, namespace and name — the same for every kind that
    /// works on it.</summary>
    public static string KeyOf(string kind, string ns, string name) => $"{kind}/{ns}/{name}";

    public async Task<IAsyncDisposable> AcquireAsync(string target, CancellationToken ct, int slots = PerTarget)
    {
        var (gate, _) = _gates.GetOrAdd(target, _ => (new SemaphoreSlim(slots, slots), slots));
        await gate.WaitAsync(ct);
        return new Slot(gate);
    }

    /// <summary>A slot now, or null when they are all taken — for an operation that must not hold a
    /// reconcile worker while it waits.</summary>
    public IAsyncDisposable? TryAcquire(string target, int slots = PerTarget)
    {
        var (gate, _) = _gates.GetOrAdd(target, _ => (new SemaphoreSlim(slots, slots), slots));
        return gate.Wait(0) ? new Slot(gate) : null;
    }

    /// <summary>The key of a target's initialization backups — one at a time, apart from its
    /// DDL slots, which a backup of several hours would otherwise hold.</summary>
    public static string BackupKeyOf(string kind, string ns, string name) => $"backup/{KeyOf(kind, ns, name)}";

    /// <summary>Drops the gate of a deleted target.</summary>
    public void Forget(string target)
    {
        // A gate still held is left to its holders: disposing it would fail their release.
        if (_gates.TryRemove(target, out var entry) && entry.Gate.CurrentCount == entry.Slots)
            entry.Gate.Dispose();
    }

    /// <summary>Drops every gate of a deleted target (its DDL sessions, its deployer, its backups).</summary>
    public void ForgetTarget(string kind, string ns, string name)
    {
        Forget(KeyOf(kind, ns, name));
        Forget($"deployer/{KeyOf(kind, ns, name)}");
        Forget(BackupKeyOf(kind, ns, name));
    }

    /// <summary>No limiter injected (tests): nothing to hold.</summary>
    public static readonly IAsyncDisposable None = new NoSlot();

    private sealed class NoSlot : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

public static class TargetLimiterExtensions
{
    /// <summary>One of the <see cref="TargetLimiter.PerTarget"/> DDL slots of a target, held
    /// for the pass's work on it; nothing when no limiter is injected.</summary>
    public static async Task<IAsyncDisposable> ForTargetAsync(this TargetLimiter? limiter, string kind, string ns, string name, CancellationToken ct)
        => limiter is null ? TargetLimiter.None : await limiter.AcquireAsync(TargetLimiter.KeyOf(kind, ns, name), ct);
}
