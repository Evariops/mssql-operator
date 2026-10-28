using System.Collections.Concurrent;
using System.Text.Json;
using k8s;
using k8s.Models;

namespace MsSqlOperator.Services.Breakout;

/// <summary>
/// The SQL pods in Terminating, kept from a watch of the pods of each selector, and
/// a signal on every change. The breakout listed every SQL pod of the cluster every 5 s to find them;
/// it now wakes on the change, and its period is only a safety net that also lets relocationDelay
/// elapse. Until both selectors have been listed once, it is not <see cref="Seeded"/>: the caller lists.
/// </summary>
public sealed class TerminatingPodTracker
{
    private readonly ConcurrentDictionary<(string Selector, string Uid), V1Pod> _terminating = new();
    private readonly ConcurrentDictionary<string, byte> _seeded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly int _selectors;

    public TerminatingPodTracker(int selectors) => _selectors = selectors;

    /// <summary>Every selector has been listed at least once.</summary>
    public bool Seeded => _seeded.Count >= _selectors;

    /// <summary>A relist of <paramref name="selector"/>: its pods in Terminating replace what was kept.</summary>
    public void Relist(string selector, IReadOnlyList<JsonElement> items)
    {
        foreach (var key in _terminating.Keys.Where(k => k.Selector == selector).ToList())
            _terminating.TryRemove(key, out _);
        foreach (var item in items)
            Update(selector, "ADDED", item, signal: false);
        _seeded.TryAdd(selector, 0);
        Signal();
    }

    /// <summary>A watch event of <paramref name="selector"/>.</summary>
    public void Update(string selector, string eventType, JsonElement item) => Update(selector, eventType, item, signal: true);

    private void Update(string selector, string eventType, JsonElement item, bool signal)
    {
        var pod = KubernetesJson.Deserialize<V1Pod>(item.GetRawText());
        if (pod?.Metadata?.Uid is not { Length: > 0 } uid)
            return;
        var key = (selector, uid);
        var changed = eventType == "DELETED" || !pod.Metadata.DeletionTimestamp.HasValue
            ? _terminating.TryRemove(key, out _)
            : _terminating.AddOrUpdate(key, pod, (_, _) => pod) is not null;
        if (signal && changed)
            Signal();
    }

    /// <summary>The pods in Terminating now, each once.</summary>
    public IReadOnlyList<V1Pod> Snapshot() => _terminating.Values.DistinctBy(p => p.Metadata.Uid).ToList();

    private void Signal()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    /// <summary>Waits for a change, at most <paramref name="safetyTick"/>; true when one woke it.</summary>
    public Task<bool> WaitAsync(TimeSpan safetyTick, CancellationToken ct) => _signal.WaitAsync(safetyTick, ct);
}
