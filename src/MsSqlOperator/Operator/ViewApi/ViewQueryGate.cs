using System.Collections.Concurrent;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Anti-DoS layer for the aggregated view API. Every GET used to fan out into live
/// SQL queries, so a kubectl watch loop (or `kubectl get -w`) hammered the managed instances.
/// (1) A short TTL cache per (resource, namespace) absorbs repeat reads — kubectl freshness
/// does not need sub-10s. (2) A global semaphore bounds concurrent SQL fan-outs; callers that
/// cannot acquire it quickly get 429 + Retry-After instead of queuing unbounded work.
/// </summary>
public sealed class ViewQueryGate(TimeProvider? time = null)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private const int MaxConcurrentSqlFanouts = 4;
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _sql = new(MaxConcurrentSqlFanouts, MaxConcurrentSqlFanouts);
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, List<Dictionary<string, object?>> Items)> _cache = new();

    /// <summary>Returns cached items, or fetches under the concurrency gate. Null means the
    /// gate is saturated — the caller must answer 429.</summary>
    public async Task<List<Dictionary<string, object?>>?> GetOrFetchAsync(
        string resource, string? ns,
        Func<Task<List<Dictionary<string, object?>>>> fetch,
        CancellationToken ct)
    {
        var key = $"{resource}|{ns}";
        var now = _time.GetUtcNow();

        if (_cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < CacheTtl)
            return cached.Items;

        if (!await _sql.WaitAsync(AcquireTimeout, ct))
        {
            // Saturated: serve stale data if we have any (better than an error for kubectl),
            // otherwise tell the client to back off.
            return _cache.TryGetValue(key, out var stale) ? stale.Items : null;
        }

        try
        {
            // Double-check after acquiring: another request may have refreshed the entry.
            if (_cache.TryGetValue(key, out cached) && _time.GetUtcNow() - cached.FetchedAt < CacheTtl)
                return cached.Items;

            var items = await fetch();
            _cache[key] = (_time.GetUtcNow(), items);
            return items;
        }
        finally
        {
            _sql.Release();
        }
    }
}
