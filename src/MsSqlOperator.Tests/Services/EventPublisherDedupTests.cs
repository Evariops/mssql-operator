using FluentAssertions;
using k8s.Models;
using Moq;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class EventPublisherDedupTests
{
    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly FakeTimeProvider _time = new();
    private readonly EventPublisher _sut;
    private Eventsv1Event? _stored;

    public EventPublisherDedupTests()
    {
        _k8s.Setup(x => x.GetAsync<Eventsv1Event>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, CancellationToken _) => _stored);
        _k8s.Setup(x => x.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Eventsv1Event e, CancellationToken _) => { _stored = e; return e; });
        _k8s.Setup(x => x.UpdateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Eventsv1Event e, CancellationToken _) => { _stored = e; return e; });
        _sut = new EventPublisher(_k8s.Object, _time);
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

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8s.Verify(x => x.UpdateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8s.Verify(x => x.UpdateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _stored!.Series!.Count.Should().Be(3);
    }

    [Fact]
    public async Task VariableMessages_SameReason_ShareOneEvent()
    {
        // The dedup key excludes the message — variants update the note.
        var ag = Ag();

        await _sut.PublishAsync(ag, "MemoryDriftDetected", "drift: 100MB", "Warning");
        _time.SetUtcNow(_time.GetUtcNow() + TimeSpan.FromSeconds(31));
        await _sut.PublishAsync(ag, "MemoryDriftDetected", "drift: 200MB", "Warning");

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Once);
        _stored!.Note.Should().Be("drift: 200MB");
    }

    [Fact]
    public async Task DistinctReasons_ProduceSeparateEvents()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 down", "Warning");
        _stored = null; // distinct key -> GetAsync returns null again
        await _sut.PublishAsync(ag, "SplitBrainDetected", "two primaries", "Warning");

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
