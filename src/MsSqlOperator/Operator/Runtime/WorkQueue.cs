namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Rate-limited, de-duplicating work queue keyed by <c>namespace/name</c>, modelled on
/// controller-runtime's workqueue. Guarantees a single in-flight reconcile per key:
/// events arriving while a key is being processed mark it <em>dirty</em> and re-queue it once
/// <see cref="Done"/> is called. Supports delayed re-queue (requeue-after) and exponential backoff.
/// </summary>
public sealed class WorkQueue : IDisposable
{
    /// <summary>
    /// Upper bound for <see cref="AddAfter"/> delays: Task.Delay throws beyond
    /// ~49.7 days, so a long RequeueAfter (e.g. a "90d" TTL) is clamped — the reconcile at the
    /// clamped horizon recomputes the real deadline.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(24);

    // Two FIFOs under the gate, the urgent one served first, and a semaphore counting what they hold.
    // One FIFO served an AG that had just lost its primary behind every other key of its kind.
    private readonly Queue<string> _ready = new();
    private readonly Queue<string> _urgent = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly HashSet<string> _urgentKeys = [];

    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = [];
    private readonly HashSet<string> _processing = [];
    private readonly Dictionary<string, DateTimeOffset> _processingSince = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, DateTimeOffset> _enqueuedAt = [];
    private readonly Dictionary<string, Scheduled> _scheduled = [];
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxBackoff;
    private bool _disposed;

    /// <summary>One pending delayed add per key: its deadline, its timer, and an identity token so a
    /// timer that lost a race against its replacement does not fire.</summary>
    private sealed record Scheduled(DateTimeOffset Due, ITimer Timer, object Token, bool Urgent);

    public WorkQueue(TimeProvider? time = null, TimeSpan? maxBackoff = null)
    {
        _time = time ?? TimeProvider.System;
        _maxBackoff = maxBackoff ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Number of keys with a pending delayed add (requeue-after, backoff, resync).</summary>
    public int ScheduledCount
    {
        get { lock (_gate) return _scheduled.Count; }
    }

    /// <summary>Number of keys waiting to be processed.</summary>
    public int Depth
    {
        get { lock (_gate) return _dirty.Count; }
    }

    /// <summary>Age of the oldest waiting key, in seconds: the honest measure of
    /// operator saturation. Without it, a queue backed up for minutes was invisible — /readyz stayed
    /// green while no availability group was being health-checked at all.</summary>
    public double OldestAgeSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_enqueuedAt.Count == 0)
                    return 0;
                var now = _time.GetUtcNow();
                var oldest = _enqueuedAt.Values.Min();
                return Math.Max(0, (now - oldest).TotalSeconds);
            }
        }
    }

    /// <summary>Age of the oldest key being processed, in seconds. Kept apart from the
    /// pending age: a queue drained on time but whose workers are all stuck on one AG looked healthy.</summary>
    public double InFlightOldestAgeSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_processingSince.Count == 0)
                    return 0;
                return Math.Max(0, (_time.GetUtcNow() - _processingSince.Values.Min()).TotalSeconds);
            }
        }
    }

    /// <summary>Enqueue a key for immediate processing (no-op if already dirty/queued). An urgent add
    /// of a key already waiting moves it ahead.</summary>
    public void Add(string key, bool urgent = false)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var added = _dirty.Add(key);
            if (added)
                _enqueuedAt.TryAdd(key, _time.GetUtcNow());
            if (urgent && !_urgentKeys.Add(key))
                return; // already urgent
            if (!added && !urgent)
                return; // already pending
            if (_processing.Contains(key))
                return; // will be re-queued on Done
            Enqueue(key, urgent);
        }
    }

    /// <summary>Under the gate.</summary>
    private void Enqueue(string key, bool urgent)
    {
        (urgent ? _urgent : _ready).Enqueue(key);
        _available.Release();
    }

    /// <summary>
    /// Enqueue a key after a delay (used for requeue-after), clamped to <see cref="MaxDelay"/>.
    /// A key has at most one pending delayed add, and the earliest deadline wins (client-go
    /// delaying-queue semantics): the runtime calls this after every successful reconcile, so
    /// independent timers per call would add one more periodic chain per event-driven reconcile and
    /// the reconcile rate of a key would grow with its event history.
    /// </summary>
    public void AddAfter(string key, TimeSpan delay, bool urgent = false)
    {
        if (delay <= TimeSpan.Zero)
        {
            Add(key, urgent);
            return;
        }

        if (delay > MaxDelay)
            delay = MaxDelay;

        lock (_gate)
        {
            if (_disposed)
                return;

            var due = _time.GetUtcNow() + delay;
            if (_scheduled.TryGetValue(key, out var existing))
            {
                if (existing.Due <= due)
                    return; // an earlier (or equal) deadline is already pending
                existing.Timer.Dispose();
            }

            var token = new object();
            var timer = _time.CreateTimer(
                static state =>
                {
                    var (queue, k, t) = ((WorkQueue, string, object))state!;
                    queue.Fire(k, t);
                },
                (this, key, token), delay, Timeout.InfiniteTimeSpan);
            _scheduled[key] = new Scheduled(due, timer, token, urgent);
        }
    }

    private void Fire(string key, object token)
    {
        bool urgent;
        lock (_gate)
        {
            if (_disposed || !_scheduled.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Token, token))
                return; // replaced by an earlier deadline, cancelled, or disposed
            _scheduled.Remove(key);
            entry.Timer.Dispose();
            urgent = entry.Urgent;
        }

        Add(key, urgent);
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
            await _available.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_urgent.TryDequeue(out var key) && !_ready.TryDequeue(out key))
                    continue;
                if (!_dirty.Remove(key))
                    continue; // stale entry (a key moved ahead is in both queues)
                _urgentKeys.Remove(key);
                _enqueuedAt.Remove(key);
                _processing.Add(key);
                _processingSince[key] = _time.GetUtcNow();
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
            _processingSince.Remove(key);
            if (resetBackoff)
                _failures.Remove(key);

            // Re-queued while processing → make it ready again.
            if (_dirty.Contains(key))
                Enqueue(key, _urgentKeys.Contains(key));
        }
    }

    /// <summary>
    /// Drops the failure bookkeeping of a deleted object: failure counts of keys that error
    /// out and then get deleted must not accumulate forever.
    ///
    /// It used to drop the DIRTY flag too, and that lost work. The sequence is real: a CR
    /// is deleted, the worker sees the 404 and calls Forget; meanwhile the user re-creates a CR of the
    /// same name, so the watch marks the key dirty again — and Forget wipes exactly that flag. The
    /// re-created resource is then never reconciled (nothing else will ever enqueue it) until the
    /// 5-minute resync happens to relist. The pending flag is now preserved: a spurious reconcile of a
    /// key that really is gone costs one GET returning 404.
    /// </summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _failures.Remove(key);
            // The pending periodic resync of a deleted object is useless; a re-created object is
            // enqueued by its watch event and schedules its own.
            if (_scheduled.Remove(key, out var entry))
                entry.Timer.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Delayed re-queues must not outlive the runtime that scheduled them.
            foreach (var entry in _scheduled.Values)
                entry.Timer.Dispose();
            _scheduled.Clear();
            // Wakes the readers, which see the queue disposed.
            _available.Release(int.MaxValue / 2);
        }
    }
}
