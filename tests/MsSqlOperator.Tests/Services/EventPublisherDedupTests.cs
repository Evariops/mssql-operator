using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class EventPublisherDedupTests
{
    // Store-mode: deterministic event names (per uid/reason/type) make Get/Create/Update behave
    // like a real API server — dedup and distinct-key cases fall out naturally.
    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FakeTimeProvider _time = new();
    private readonly EventPublisher _sut;

    public EventPublisherDedupTests()
    {
        _sut = new EventPublisher(_k8s, _time);
    }

    private static SqlAvailabilityGroup Ag() => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default", Uid = "uid-1" }
    };

    [Fact]
    public async Task RepeatsInsideTtl_AreDroppedWithoutApiCalls()
    {
        // No GET/UPDATE per publish in steady state.
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");

        Assert.Single(_k8s.Created<Eventsv1Event>());
        Assert.Empty(_k8s.Updated<Eventsv1Event>());
    }

    [Fact]
    public async Task RepeatsBeyondTtl_AggregateIntoSeries()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(31));
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(31));
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");

        Assert.Single(_k8s.Created<Eventsv1Event>());
        Assert.Equal(2, _k8s.Updated<Eventsv1Event>().Count());
        Assert.Equal(3, _k8s.Created<Eventsv1Event>().Single().Series!.Count);
    }

    [Fact]
    public async Task VariableMessages_SameReason_ReplaceTheEvent_NeverRewriteItsNote()
    {
        // The dedup key excludes the message. The note of an events.k8s.io/v1 Event is
        // immutable (the API server answers 422): a variant replaces the event, it is never updated.
        var ag = Ag();

        await _sut.PublishAsync(ag, "MemoryDriftDetected", "drift: 100MB", "Warning");
        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(31));
        await _sut.PublishAsync(ag, "MemoryDriftDetected", "drift: 200MB", "Warning");

        Assert.Empty(_k8s.Updated<Eventsv1Event>());
        Assert.Equal("drift: 100MB", Assert.Single(_k8s.Deleted<Eventsv1Event>()).Note);
        Assert.Equal(["drift: 100MB", "drift: 200MB"], _k8s.Created<Eventsv1Event>().Select(e => e.Note));
        Assert.Single(_k8s.Created<Eventsv1Event>().Select(e => e.Metadata.Name).Distinct());
    }

    [Fact]
    public async Task DistinctReasons_ProduceSeparateEvents()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 down", "Warning");
        await _sut.PublishAsync(ag, "SplitBrainDetected", "two primaries", "Warning");

        Assert.Equal(2, _k8s.Created<Eventsv1Event>().Count());
    }

    [Fact]
    public async Task NewMessageInsideTtl_IsPublished_NotDropped()
    {
        // A second event of the reason with another message (FailoverFailed, then its cause) was
        // dropped for 30 s: the event on display said what was no longer true.
        var ag = Ag();

        await _sut.PublishAsync(ag, "FailoverFailed", "promotion of ag-1 timed out", "Warning");
        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(3));
        await _sut.PublishAsync(ag, "FailoverFailed", "ag-1 is not synchronized", "Warning");

        Assert.Equal(["promotion of ag-1 timed out", "ag-1 is not synchronized"], _k8s.Created<Eventsv1Event>().Select(e => e.Note));
    }

    [Fact]
    public async Task NewMessages_InBurst_AreRateLimited()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "Progress", "1%", "Normal");
        await _sut.PublishAsync(ag, "Progress", "2%", "Normal");

        Assert.Single(_k8s.Created<Eventsv1Event>());
    }
}
