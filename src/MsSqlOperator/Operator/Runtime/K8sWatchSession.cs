using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Drives a single Kubernetes watch session over the non-obsolete first-class
/// <c>WatchList…Async</c> methods, which return an <see cref="IAsyncEnumerable{T}"/> of
/// <c>(WatchEventType, item)</c> and read the line-separated watch stream correctly.
/// <para>
/// The earlier approach (reading the <c>WithHttpMessages</c> response via <c>ReadAsStreamAsync()</c>)
/// failed with "ArgumentException: Stream was not readable" because a watch body is wrapped in the
/// client's internal <c>LineSeparatedHttpContent</c>. Each typed event is re-serialized to a
/// <see cref="JsonElement"/> so the type-agnostic <see cref="ResourceWatcher"/> keeps its
/// JSON-based seeding/routing handlers.
/// </para>
/// </summary>
internal static class K8sWatchSession
{
    /// <summary>
    /// Consumes <paramref name="events"/> until the session ends. Returns the resourceVersion to
    /// resume from on a clean server-side close, or null when a relist is required (in-stream
    /// ERROR / 410 Gone).
    /// </summary>
    public static async Task<string?> RunAsync<T>(
        IAsyncEnumerable<(WatchEventType Type, T Item)> events,
        string kind,
        ILogger logger,
        Action onActivity,
        Action<string, JsonElement> emit,
        CancellationToken ct)
    {
        string? lastRv = null;
        var forceRelist = false;

        try
        {
            await foreach (var (eventType, item) in events.WithCancellation(ct).ConfigureAwait(false))
            {
                onActivity();

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(KubernetesJson.Serialize(item));
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Skipping unserializable {Kind} watch event ({Type})", kind, eventType);
                    continue;
                }

                // The emit callbacks consume the element before returning (as the relist does): the
                // document goes back to the pool after each event instead of being left to the GC.
                using var _ = doc;
                var obj = doc.RootElement;
                var rv = RvOf(obj);
                switch (eventType)
                {
                    case WatchEventType.Added:
                    case WatchEventType.Modified:
                    case WatchEventType.Deleted:
                        try
                        {
                            emit(EventName(eventType), obj);
                        }
                        catch (Exception e)
                        {
                            // A malformed object must not destroy the watch.
                            logger.LogError(e, "Skipping malformed {Kind} watch event ({Type})", kind, eventType);
                        }
                        lastRv = rv ?? lastRv;
                        break;

                    case WatchEventType.Bookmark:
                        // Bookmarks advance the resume point without traffic.
                        lastRv = rv ?? lastRv;
                        break;

                    case WatchEventType.Error:
                        // Typically a v1.Status (e.g. 410 Gone mid-stream): force a relist.
                        logger.LogWarning("{Kind} watch error event; relisting", kind);
                        forceRelist = true;
                        break;
                }
            }
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Gone)
        {
            // 410 Gone: resourceVersion too old — fall through to a full relist.
            forceRelist = true;
        }

        return forceRelist ? null : lastRv;
    }

    private static string EventName(WatchEventType t) => t switch
    {
        WatchEventType.Added => "ADDED",
        WatchEventType.Modified => "MODIFIED",
        WatchEventType.Deleted => "DELETED",
        _ => t.ToString(),
    };

    private static string? RvOf(JsonElement obj)
        => obj.TryGetProperty("metadata", out var m) && m.TryGetProperty("resourceVersion", out var rv)
            ? rv.GetString()
            : null;
}
