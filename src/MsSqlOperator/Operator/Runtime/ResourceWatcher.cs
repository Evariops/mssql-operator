using System.Text.Json;
using k8s.Autorest;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// The robust watch mechanism factored out of <c>EntityRuntime</c> (reused by the owned-resource watch):
/// list → watch → relist, with server-side rotation (<c>timeoutSeconds</c>), bookmark-driven
/// resourceVersion resume, 410-Gone relist, per-line deserialization tolerance, and failure
/// escalation. Shared by the CR watch (<c>EntityRuntime</c>) and the owned-resource watch
/// (<c>OwnedResourceWatcher</c>) so the fragile streaming logic lives in exactly one place.
/// </summary>
/// <param name="kind">Human label for logs (the watched Kind).</param>
/// <param name="logger">Logger for watch lifecycle and tolerance diagnostics.</param>
/// <param name="openList">
/// Opens a list (<c>watch=false</c>) or a watch stream (<c>watch=true</c>) at an optional
/// resourceVersion. The caller bakes in any label selector. The returned response is disposed here.
/// </param>
/// <param name="onRelist">Receives every item of a full relist (for seeding / deletion detection).</param>
/// <param name="onEvent">Receives each ADDED/MODIFIED/DELETED watch event (type, object element).</param>
/// <param name="time">Clock for <see cref="LastActivity"/> (injectable for tests).</param>
/// <param name="watchTimeoutSeconds">Server-side rotation timeout; also the readiness zombie base.</param>
public sealed class ResourceWatcher(
    string kind,
    ILogger logger,
    Func<bool, string?, CancellationToken, Task<HttpOperationResponse>> openList,
    Action<IReadOnlyList<JsonElement>> onRelist,
    Action<string, JsonElement> onEvent,
    TimeProvider? time = null,
    int watchTimeoutSeconds = 300)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _consecutiveFailures;

    /// <summary>The server-side watch rotation timeout (seconds); the readiness zombie check
    /// flags a stream with no activity for &gt; 2× this value.</summary>
    public int WatchTimeoutSeconds { get; } = watchTimeoutSeconds;

    /// <summary>
    /// Wall-clock time of the last watch activity (relist, event, or bookmark). Drives the
    /// owned-watch zombie readiness check: no activity for &gt; 2× the server-side
    /// timeout means the stream is a silent zombie.
    /// </summary>
    public DateTimeOffset LastActivity { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        LastActivity = _time.GetUtcNow();
        string? resourceVersion = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Full relist only when we have no resumable resourceVersion (startup, error,
                // 410 Gone). A clean server-side rotation resumes from the last seen RV.
                resourceVersion ??= await RelistAsync(ct).ConfigureAwait(false);
                resourceVersion = await WatchFromAsync(resourceVersion, ct).ConfigureAwait(false);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                resourceVersion = null; // force a full relist
                var failures = ++_consecutiveFailures;
                // Escalate after repeated failures — a perpetual Warning hides outages.
                if (failures >= 5)
                    logger.LogError(e, "{Kind} watch failed {Failures} consecutive times; relisting in 5s", kind, failures);
                else
                    logger.LogWarning(e, "{Kind} watch failed; relisting in 5s", kind);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lists everything, hands the items to <c>onRelist</c>, and returns the list
    /// resourceVersion to start the watch from.</summary>
    private async Task<string?> RelistAsync(CancellationToken ct)
    {
        using var resp = await openList(false, null, ct).ConfigureAwait(false);
        var body = await resp.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        var items = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var element in arr.EnumerateArray())
                items.Add(element);

        onRelist(items);
        LastActivity = _time.GetUtcNow();

        return doc.RootElement.TryGetProperty("metadata", out var meta)
               && meta.TryGetProperty("resourceVersion", out var rv)
            ? rv.GetString()
            : null;
    }

    /// <summary>
    /// Runs one watch session. Returns the resourceVersion to resume from on a clean close
    /// (server-side rotation), or null when a full relist is required (410 Gone).
    /// </summary>
    private async Task<string?> WatchFromAsync(string? resourceVersion, CancellationToken ct)
    {
        var lastRv = resourceVersion;
        try
        {
            using var resp = await openList(true, resourceVersion, ct).ConfigureAwait(false);

            // The watch response is a stream of newline-delimited JSON events
            // ({"type":"ADDED","object":{...}}). Read it directly: the SDK's Watch/WatchAsync
            // extensions are obsolete and hide the per-line handling we need.
            await using var stream = await resp.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException e)
                {
                    // A poisoned line must not destroy the watch for the whole kind.
                    logger.LogError(e, "Skipping malformed {Kind} watch line", kind);
                    continue;
                }

                using (doc)
                {
                    var eventType = doc.RootElement.GetProperty("type").GetString();
                    var objectElement = doc.RootElement.GetProperty("object");
                    LastActivity = _time.GetUtcNow();

                    switch (eventType)
                    {
                        case "ADDED" or "MODIFIED" or "DELETED":
                            try
                            {
                                onEvent(eventType!, objectElement);
                            }
                            catch (Exception e)
                            {
                                // A malformed object must not destroy the watch.
                                logger.LogError(e, "Skipping malformed {Kind} watch event ({Type})", kind, eventType);
                                continue;
                            }
                            lastRv = RvOf(objectElement) ?? lastRv;
                            break;

                        case "BOOKMARK":
                            // Bookmarks advance the resume point without traffic.
                            lastRv = RvOf(objectElement) ?? lastRv;
                            break;

                        case "ERROR":
                            // Typically a v1.Status (e.g. 410 Gone mid-stream): force a relist.
                            logger.LogWarning("{Kind} watch error event: {Status}", kind, objectElement.GetRawText());
                            return null;
                    }
                }
            }
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            // 410 Gone: resourceVersion too old, fall through to a full relist.
            return null;
        }

        // Clean close (server-side rotation): resume from the last seen RV.
        return lastRv;
    }

    private static string? RvOf(JsonElement obj)
        => obj.TryGetProperty("metadata", out var m) && m.TryGetProperty("resourceVersion", out var rv)
            ? rv.GetString()
            : null;
}
