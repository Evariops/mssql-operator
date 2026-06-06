using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Client;

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
        var ns = resource.Metadata.NamespaceProperty;

        // Deterministic name keyed on (object, reason, message) so a repeating condition aggregates
        // into a single event with an incrementing count instead of spamming etcd with one event per
        // reconcile.
        var name = $"{resource.Metadata.Name}.{StableSuffix(resource.Metadata.Uid, reason, message)}";

        try
        {
            var existing = await kubernetesClient.GetAsync<Corev1Event>(name, ns, ct);
            if (existing != null)
            {
                existing.Count = (existing.Count ?? 1) + 1;
                existing.LastTimestamp = now;
                existing.Message = message;
                await kubernetesClient.UpdateAsync(existing, ct);
                return;
            }
        }
        catch (Exception)
        {
            // Fall through to create — event publishing is best-effort.
        }

        var evt = new Corev1Event
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            InvolvedObject = new V1ObjectReference
            {
                ApiVersion = resource.ApiVersion,
                Kind = resource.Kind,
                Name = resource.Metadata.Name,
                NamespaceProperty = ns,
                Uid = resource.Metadata.Uid
            },
            Reason = reason,
            Message = message,
            Type = type,
            Count = 1,
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

    private static string StableSuffix(string? uid, string reason, string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{uid}|{reason}|{message}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
