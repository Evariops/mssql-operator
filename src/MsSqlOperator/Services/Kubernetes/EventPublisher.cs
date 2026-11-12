using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

public interface IEventPublisher
{
    Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default);
}

/// <summary>
/// Publishes events.k8s.io/v1 Events (core/v1 Event is deprecated) with series
/// aggregation. Deduplication is keyed on (uid, reason, type) WITHOUT the message:
/// a variable message (e.g. an exception text) replaces the one event of that reason instead of
/// creating a new object per variant. An in-memory TTL gate drops repeats of the same message
/// within 30 seconds (a new message within 2 s) so steady-state reconciles stop issuing a GET
/// before every publish.
/// </summary>
public class EventPublisher(
    IKubernetesClient kubernetesClient,
    TimeProvider? time = null,
    ILogger<EventPublisher>? logger = null) : IEventPublisher
{
    private static readonly TimeSpan RepublishTtl = TimeSpan.FromSeconds(30);

    /// <summary>A new message of the same reason replaces the event at most this often.</summary>
    private static readonly TimeSpan ChangedMessageTtl = TimeSpan.FromSeconds(2);

    private sealed record Published(DateTime At, string Message);

    private readonly ConcurrentDictionary<string, Published> _lastPublished = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>Drops dedup entries older than the TTL. Swept at most once a minute so
    /// the publish path stays cheap.</summary>
    private void PurgeExpired(DateTime now)
    {
        if (now - _lastPurge < TimeSpan.FromMinutes(1))
            return;
        _lastPurge = now;

        foreach (var (key, published) in _lastPublished)
        {
            if (now - published.At >= RepublishTtl)
                _lastPublished.TryRemove(key, out _);
        }
    }

    public async Task PublishAsync(IKubernetesObject<V1ObjectMeta> resource, string reason, string message, string type = "Normal", CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var ns = resource.Metadata.NamespaceProperty;

        // Deterministic name keyed on (object, reason, type) so a repeating condition aggregates
        // into a single event with an incrementing series instead of spamming etcd.
        var name = $"{resource.Metadata.Name}.{StableSuffix(resource.Metadata.Uid, reason, type)}";

        // Drop repeats inside the TTL — no API round-trip at all. Only repeats of the same message: a
        // second event of the reason with another message (FailoverFailed then its cause, a phase
        // that moved on) was dropped for 30 s, and the event shown said what was no longer true.
        if (_lastPublished.TryGetValue(name, out var last)
            && now - last.At < (last.Message == message ? RepublishTtl : ChangedMessageTtl))
            return;
        _lastPublished[name] = new Published(now, message);

        // The dedup map was never purged. Its keys embed the resource UID, so every CR
        // ever reconciled — including deleted ones — left an entry behind, in a process capped at
        // 256Mi. Sweep the expired entries; they are useless past the TTL by construction.
        PurgeExpired(now);

        try
        {
            var existing = await kubernetesClient.GetAsync<Eventsv1Event>(name, ns, ct);
            if (existing != null)
            {
                if (existing.Note == message)
                {
                    existing.Series = new Eventsv1EventSeries
                    {
                        Count = (existing.Series?.Count ?? 1) + 1,
                        LastObservedTime = now,
                    };
                    await kubernetesClient.UpdateAsync(existing, ct);
                    return;
                }

                // The note of an events.k8s.io/v1 Event is immutable: rewriting it was refused (422)
                // and every later occurrence with another message was lost — the incidents whose
                // messages change from pass to pass went silent. The event is replaced instead.
                await kubernetesClient.DeleteAsync(existing, ct);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Event publishing is best-effort, don't fail reconciliation — but say so: a
            // refused update (missing RBAC verb) used to hide every occurrence after the first one.
            logger?.LogWarning(ex, "Could not publish event {Reason} on {Kind} {Namespace}/{Name}",
                reason, resource.Kind, ns, resource.Metadata.Name);
        }
    }

    private static string StableSuffix(string? uid, string reason, string type)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{uid}|{reason}|{type}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
