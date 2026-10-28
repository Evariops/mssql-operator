using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>Events are the first writes to go under pressure; Warnings the last of them.</summary>
public class QueuedEventPublisherTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private static readonly SqlInstance Owner = new() { Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Uid = "u" } };

    [Fact]
    public async Task QueueFull_AWarningEvictsTheOldestNormal_ANormalIsDropped()
    {
        using var publisher = new QueuedEventPublisher(new EventPublisher(_k8s), capacity: 2);

        await publisher.PublishAsync(Owner, "N1", "n1");
        await publisher.PublishAsync(Owner, "N2", "n2");
        await publisher.PublishAsync(Owner, "W1", "w1", "Warning");
        await publisher.PublishAsync(Owner, "N3", "n3");
        await publisher.DrainAsync(CancellationToken.None);

        // The Warning is written first; N1 was evicted for it, N3 found the queue full.
        Assert.Equal(["W1", "N2"], _k8s.CreateCalls.OfType<Eventsv1Event>().Select(e => e.Reason).ToList());
    }

    [Fact]
    public async Task PublishAsync_DoesNotWaitForTheWrite()
    {
        using var publisher = new QueuedEventPublisher(new EventPublisher(_k8s));

        await publisher.PublishAsync(Owner, "R", "m");

        Assert.Empty(_k8s.CreateCalls);
        Assert.Equal(1, publisher.Pending);
    }

    [Fact]
    public void TokenBucket_RefillsAtItsRate()
    {
        var clock = new FakeTimeProvider();
        var bucket = new TokenBucket(rate: 1, burst: 2, clock);

        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(bucket.TryTake());
    }

    [Theory]
    [InlineData(typeof(SqlAvailabilityGroup), true)]
    [InlineData(typeof(V1Node), true)]
    [InlineData(typeof(V1PersistentVolumeClaim), true)]
    [InlineData(typeof(V1StatefulSet), true)]
    [InlineData(typeof(V1PodDisruptionBudget), true)]
    [InlineData(typeof(GenericCiliumNetworkPolicy), true)]
    [InlineData(typeof(V1Secret), false)]
    [InlineData(typeof(V1ConfigMap), false)]
    public void NoPut_OnKindsOthersWriteToo(Type type, bool refused)
        => Assert.Equal(refused, JsonPatch.NoPut(type) is not null);
}
