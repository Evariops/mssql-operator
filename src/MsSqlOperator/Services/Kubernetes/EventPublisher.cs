using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace MsSqlOperator.Services.Kubernetes;

public interface IEventPublisher
{
    Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default);
}

public class EventPublisher(IKubernetesClient kubernetesClient) : IEventPublisher
{
    public async Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var evt = new Corev1Event
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{resource.Metadata.Name}.{Guid.NewGuid():N}",
                NamespaceProperty = resource.Metadata.NamespaceProperty
            },
            InvolvedObject = new V1ObjectReference
            {
                ApiVersion = resource.ApiVersion,
                Kind = resource.Kind,
                Name = resource.Metadata.Name,
                NamespaceProperty = resource.Metadata.NamespaceProperty,
                Uid = resource.Metadata.Uid
            },
            Reason = reason,
            Message = message,
            Type = type,
            FirstTimestamp = now,
            LastTimestamp = now,
            Source = new V1EventSource { Component = "mssql-operator" },
            ReportingComponent = "mssql-operator",
            ReportingInstance = Environment.MachineName
        };

        try
        {
            await kubernetesClient.CreateAsync(evt, ct);
        }
        catch (Exception)
        {
            // Event publishing is best-effort, don't fail reconciliation
        }
    }
}
