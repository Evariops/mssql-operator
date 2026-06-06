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
    private readonly EventPublisher _sut;
    private Corev1Event? _stored;

    public EventPublisherDedupTests()
    {
        _k8s.Setup(x => x.GetAsync<Corev1Event>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, CancellationToken _) => _stored);
        _k8s.Setup(x => x.CreateAsync(It.IsAny<Corev1Event>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Corev1Event e, CancellationToken _) => { _stored = e; return e; });
        _k8s.Setup(x => x.UpdateAsync(It.IsAny<Corev1Event>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Corev1Event e, CancellationToken _) => { _stored = e; return e; });
        _sut = new EventPublisher(_k8s.Object);
    }

    private static SqlAvailabilityGroup Ag() => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default", Uid = "uid-1" }
    };

    [Fact]
    public async Task RepeatedIdenticalEvent_AggregatesWithCount_InsteadOfCreatingDuplicates()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");
        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 is DISCONNECTED", "Warning");

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Corev1Event>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8s.Verify(x => x.UpdateAsync(It.IsAny<Corev1Event>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _stored!.Count.Should().Be(3);
    }

    [Fact]
    public async Task DistinctReasons_ProduceSeparateEvents()
    {
        var ag = Ag();

        await _sut.PublishAsync(ag, "ReplicaDisconnected", "ag-1 down", "Warning");
        _stored = null; // distinct key → GetAsync returns null again
        await _sut.PublishAsync(ag, "SplitBrainDetected", "two primaries", "Warning");

        _k8s.Verify(x => x.CreateAsync(It.IsAny<Corev1Event>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
