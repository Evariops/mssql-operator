using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Events are the lowest class of writes — under pressure they go first, so that the
/// writes that steer the cluster (control, then status) keep the API server's attention. A reconcile
/// no longer waits for its events: they are queued (bounded) and written by one worker at a bounded
/// rate (token bucket). When the queue is full a Normal event is dropped; a Warning evicts the oldest
/// Normal one, and is dropped only when nothing but Warnings is queued. Every loss is counted
/// (<c>mssqloperator_writes_dropped_total{class="Event"}</c>).
/// </summary>
public sealed class QueuedEventPublisher(EventPublisher inner, Metrics.OperatorMetrics? metrics = null,
    ILogger<QueuedEventPublisher>? logger = null, int capacity = 1000, double eventsPerSecond = 20, int burst = 50)
    : BackgroundService, IEventPublisher
{
    private readonly Lock _gate = new();
    private readonly Queue<Item> _warnings = new();
    private readonly LinkedList<Item> _normals = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TokenBucket _bucket = new(eventsPerSecond, burst, TimeProvider.System);

    private sealed record Item(IKubernetesObject<V1ObjectMeta> Resource, string Reason, string Message, string Type);

    public int Pending { get { lock (_gate) return _warnings.Count + _normals.Count; } }

    public Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        var item = new Item(resource, reason, message, type);
        var warning = type == "Warning";
        lock (_gate)
        {
            if (_warnings.Count + _normals.Count >= capacity)
            {
                if (warning && _normals.First is { } oldest)
                {
                    _normals.RemoveFirst();
                    Dropped(oldest.Value);
                }
                else
                {
                    Dropped(item);
                    return Task.CompletedTask;
                }
            }
            if (warning) _warnings.Enqueue(item);
            else _normals.AddLast(item);
        }
        _signal.Release();
        return Task.CompletedTask;
    }

    private void Dropped(Item item)
    {
        metrics?.WriteDropped("Event", item.Type);
        logger?.LogDebug("Event {Reason} on {Kind} {Name} dropped: the event queue is full", item.Reason, item.Resource.Kind,
            item.Resource.Metadata.Name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken);
            Item? item;
            lock (_gate)
            {
                if (_warnings.TryDequeue(out item)) { }
                else if (_normals.First is { } first) { item = first.Value; _normals.RemoveFirst(); }
            }
            if (item is null)
                continue;
            await _bucket.TakeAsync(stoppingToken);
            await inner.PublishAsync(item.Resource, item.Reason, item.Message, item.Type, stoppingToken);
        }
    }

    /// <summary>Writes what is queued, for a clean shutdown and for tests.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            Item? item;
            lock (_gate)
            {
                if (_warnings.TryDequeue(out item)) { }
                else if (_normals.First is { } first) { item = first.Value; _normals.RemoveFirst(); }
            }
            if (item is null)
                return;
            await inner.PublishAsync(item.Resource, item.Reason, item.Message, item.Type, ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await DrainAsync(cancellationToken); }
        catch (OperationCanceledException) { /* shutdown deadline */ }
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }
}

/// <summary>A token bucket — <c>rate</c> tokens a second, up to <c>burst</c>.</summary>
public sealed class TokenBucket(double rate, int burst, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private double _tokens = burst;
    private DateTimeOffset _last = clock.GetUtcNow();

    public bool TryTake()
    {
        lock (_gate)
        {
            Refill();
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }

    public async Task TakeAsync(CancellationToken ct)
    {
        while (!TryTake())
            await Task.Delay(TimeSpan.FromSeconds(1 / rate), clock, ct);
    }

    private void Refill()
    {
        var now = clock.GetUtcNow();
        _tokens = Math.Min(burst, _tokens + (now - _last).TotalSeconds * rate);
        _last = now;
    }
}
