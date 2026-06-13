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
/// <param name="listJson">
/// Performs a full (non-watch) list and returns the response body as JSON. MUST come from the
/// already-deserialized list object (e.g. <c>KubernetesJson.Serialize(await …ListAsync())</c>),
/// NOT a re-read of the HTTP stream: the k8s client consumes a non-watch response's content stream
/// to build its typed body, so reading it again throws "stream already consumed".
/// </param>
/// <param name="watchSession">
/// Runs one watch session from a resourceVersion: it pumps each event to the supplied emit callback
/// (after marking activity) and returns the resourceVersion to resume from, or null to force a
/// relist. Backed by <see cref="K8sWatchSession"/> (the watch body cannot be read as a raw stream).
/// </param>
/// <param name="onRelist">Receives every item of a full relist (for seeding / deletion detection).</param>
/// <param name="onEvent">Receives each ADDED/MODIFIED/DELETED watch event (type, object element).</param>
/// <param name="time">Clock for <see cref="LastActivity"/> (injectable for tests).</param>
/// <param name="watchTimeoutSeconds">Server-side rotation timeout; also the readiness zombie base.</param>
/// <param name="stopIfResourceAbsent">
/// When true, a 404 (resource type not served — e.g. an optional CRD that isn't installed) stops
/// this watcher gracefully instead of retrying forever. Used by the owned-resource watch for
/// optional child types like CiliumNetworkPolicy.
/// </param>
public sealed class ResourceWatcher(
    string kind,
    ILogger logger,
    Func<CancellationToken, Task<string>> listJson,
    Func<string?, Action, Action<string, JsonElement>, CancellationToken, Task<string?>> watchSession,
    Action<IReadOnlyList<JsonElement>> onRelist,
    Action<string, JsonElement> onEvent,
    TimeProvider? time = null,
    int watchTimeoutSeconds = 300,
    bool stopIfResourceAbsent = false)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _consecutiveFailures;

    /// <summary>The server-side watch rotation timeout (seconds); the readiness zombie check
    /// flags a stream with no activity for &gt; 2× this value.</summary>
    public int WatchTimeoutSeconds { get; } = watchTimeoutSeconds;

    /// <summary>True once the watcher has stopped because its resource type is not served (404).
    /// Such a watcher is excluded from the readiness zombie check.</summary>
    public bool Stopped { get; private set; }

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
            catch (HttpOperationException e) when (stopIfResourceAbsent && e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // The resource type is not served (e.g. an optional CRD that is not installed):
                // disable this watcher quietly rather than logging an error every 5s forever.
                logger.LogInformation("{Kind}: resource type not served (404); watch disabled", kind);
                Stopped = true;
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
        var body = await listJson(ct).ConfigureAwait(false);
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
    /// Runs one watch session via the supplied delegate (backed by <see cref="K8sWatchSession"/>).
    /// Returns the resourceVersion to resume from on a clean close, or null to force a relist.
    /// </summary>
    private Task<string?> WatchFromAsync(string? resourceVersion, CancellationToken ct)
        => watchSession(resourceVersion, () => LastActivity = _time.GetUtcNow(), onEvent, ct);
}
