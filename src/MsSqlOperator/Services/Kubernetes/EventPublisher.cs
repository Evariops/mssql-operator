using System.Collections.Concurrent;
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

/// <summary>
/// Publishes events.k8s.io/v1 Events (core/v1 Event is deprecated) with series
/// aggregation. Deduplication is keyed on (uid, reason, type) WITHOUT the message:
/// a variable message (e.g. an exception text) updates the note of one event instead of
/// creating a new object per variant. An in-memory TTL gate drops repeats within
/// 30 seconds so steady-state reconciles stop issuing a GET before every publish.
/// </summary>
public class EventPublisher(IKubernetesClient kubernetesClient, TimeProvider? time = null) : IEventPublisher
{
    private static readonly TimeSpan RepublishTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTime> _lastPublished = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var ns = resource.Metadata.NamespaceProperty;

        // Deterministic name keyed on (object, reason, type) so a repeating condition aggregates
        // into a single event with an incrementing series instead of spamming etcd.
        var name = $"{resource.Metadata.Name}.{StableSuffix(resource.Metadata.Uid, reason, type)}";

        // Drop repeats inside the TTL — no API round-trip at all.
        if (_lastPublished.TryGetValue(name, out var last) && now - last < RepublishTtl)
            return;
        _lastPublished[name] = now;

        try
        {
            var existing = await kubernetesClient.GetAsync<Eventsv1Event>(name, ns, ct);
            if (existing != null)
            {
                existing.Series = new Eventsv1EventSeries
                {
                    Count = (existing.Series?.Count ?? 1) + 1,
                    LastObservedTime = now,
                };
                existing.Note = message;
                await kubernetesClient.UpdateAsync(existing, ct);
                return;
            }

            var evt = new Eventsv1Event
            {
                Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
                Regarding = new V1ObjectReference
                {
                    ApiVersion = resource.ApiVersion,
                    Kind = resource.Kind,
                    Name = resource.Metadata.Name,
                    NamespaceProperty = ns,
                    Uid = resource.Metadata.Uid
                },
                Reason = reason,
                Note = message,
                Type = type,
                Action = "Reconcile",
                EventTime = now,
                ReportingController = "mssql-operator.evariops.com",
                ReportingInstance = Environment.MachineName
            };

            await kubernetesClient.CreateAsync(evt, ct);
        }
        catch (Exception)
        {
            // Event publishing is best-effort, don't fail reconciliation
        }
    }

    private static string StableSuffix(string? uid, string reason, string type)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{uid}|{reason}|{type}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
