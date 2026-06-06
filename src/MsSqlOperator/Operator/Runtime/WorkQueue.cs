using System.Threading.Channels;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Rate-limited, de-duplicating work queue keyed by <c>namespace/name</c>, modelled on
/// controller-runtime's workqueue. Guarantees a single in-flight reconcile per key:
/// events arriving while a key is being processed mark it <em>dirty</em> and re-queue it once
/// <see cref="Done"/> is called. Supports delayed re-queue (requeue-after) and exponential backoff.
/// </summary>
public sealed class WorkQueue : IDisposable
{
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = [];
    private readonly HashSet<string> _processing = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxBackoff;

    public WorkQueue(TimeProvider? time = null, TimeSpan? maxBackoff = null)
    {
        _time = time ?? TimeProvider.System;
        _maxBackoff = maxBackoff ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Enqueue a key for immediate processing (no-op if already dirty/queued).</summary>
    public void Add(string key)
    {
        lock (_gate)
        {
            if (!_dirty.Add(key))
                return; // already pending
            if (_processing.Contains(key))
                return; // will be re-queued on Done
            _ready.Writer.TryWrite(key);
        }
    }

    /// <summary>Enqueue a key after a delay (used for requeue-after).</summary>
    public void AddAfter(string key, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            Add(key);
            return;
        }

        _ = Task.Delay(delay, _time).ContinueWith(_ => Add(key), TaskScheduler.Default);
    }

    /// <summary>Re-queue a key after an exponential backoff based on its consecutive failure count.</summary>
    public void AddWithBackoff(string key)
    {
        int failures;
        lock (_gate)
        {
            failures = _failures[key] = _failures.GetValueOrDefault(key) + 1;
        }

        var seconds = Math.Min(_maxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 8)));
        AddAfter(key, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Awaits the next key ready for processing and marks it in-flight.</summary>
    public async ValueTask<string> GetAsync(CancellationToken ct)
    {
        while (true)
        {
            var key = await _ready.Reader.ReadAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_dirty.Remove(key))
                    continue; // stale entry
                _processing.Add(key);
                return key;
            }
        }
    }

    /// <summary>
    /// Marks a key's processing as finished. If new events arrived while it was processing,
    /// it is re-queued. Pass <paramref name="resetBackoff"/> on success to clear its failure count.
    /// </summary>
    public void Done(string key, bool resetBackoff)
    {
        lock (_gate)
        {
            _processing.Remove(key);
            if (resetBackoff)
                _failures.Remove(key);

            // Re-queued while processing → make it ready again.
            if (_dirty.Contains(key))
                _ready.Writer.TryWrite(key);
        }
    }

    public void Dispose() => _ready.Writer.TryComplete();
}
