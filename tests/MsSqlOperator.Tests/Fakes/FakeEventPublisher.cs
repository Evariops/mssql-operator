using k8s;
using k8s.Models;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IEventPublisher"/>: records every published event.</summary>
public sealed class FakeEventPublisher : IEventPublisher
{
    public readonly List<(IKubernetesObject<V1ObjectMeta> Resource, string Reason, string Message, string Type)> Calls = [];

    public Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        Calls.Add((resource, reason, message, type));
        return Task.CompletedTask;
    }

    /// <summary>Reasons of all published events, in order — convenient for assertions.</summary>
    public IEnumerable<string> Reasons => Calls.Select(c => c.Reason);
}
