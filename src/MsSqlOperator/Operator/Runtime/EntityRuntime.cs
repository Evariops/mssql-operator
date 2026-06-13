using System.Collections.Concurrent;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Reconciliation runtime for a single custom-resource type: watches the resource cluster-wide
/// (list → watch → relist on disconnect/410), feeds a <see cref="WorkQueue"/>, and runs worker
/// loops that fetch the fresh object, manage the finalizer, and dispatch to the controller.
/// </summary>
public sealed class EntityRuntime<TEntity>(
    IKubernetes raw,
    IServiceProvider services,
    ILogger<EntityRuntime<TEntity>> logger,
    int workerCount = 2,
    TimeSpan defaultResync = default)
    : IEntityRuntime, IDisposable
    where TEntity : class, IKubernetesObject<V1ObjectMeta>, new()
{
    private readonly GroupVersionKind _gvk = GroupVersionKind.Of<TEntity>();
    private readonly WorkQueue _queue = new();
    private readonly ConcurrentDictionary<string, TEntity> _lastSeen = new();

    public string EntityKind => _gvk.Kind;

    public async Task RunAsync(CancellationToken ct)
    {
        var workers = Enumerable.Range(0, workerCount).Select(i => WorkerLoop(i, ct)).ToArray();
        var watch = WatchLoop(ct);
        await Task.WhenAll(workers.Append(watch)).ConfigureAwait(false);
    }

    // ── Watch (list → watch → relist) ───────────────────────────────────

    /// <summary>Server-side watch rotation: the API server closes the stream after
    /// this many seconds, so a half-dead connection can never become a silent zombie.</summary>
    private const int WatchTimeoutSeconds = 300;

    private int _consecutiveWatchFailures;

    private async Task WatchLoop(CancellationToken ct)
    {
        string? resourceVersion = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Full relist only when we have no resumable resourceVersion (startup, error,
                // 410 Gone). A clean server-side rotation resumes from the last seen RV
                // instead of re-listing everything.
                resourceVersion ??= await RelistAsync(ct).ConfigureAwait(false);
                resourceVersion = await WatchFrom(resourceVersion, ct).ConfigureAwait(false);
                _consecutiveWatchFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                resourceVersion = null; // force a full relist
                var failures = ++_consecutiveWatchFailures;
                // Escalate after repeated failures — a perpetual Warning hides outages.
                if (failures >= 5)
                    logger.LogError(e, "{Kind} watch failed {Failures} consecutive times; relisting in 5s", _gvk.Kind, failures);
                else
                    logger.LogWarning(e, "{Kind} watch failed; relisting in 5s", _gvk.Kind);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Lists everything, seeds the queue, detects deletions missed while the watch was down
    /// (known keys absent from the list are enqueued — the worker sees the 404 and
    /// fires DeletedAsync + purges bookkeeping), and returns the list resourceVersion.
    /// </summary>
    private async Task<string?> RelistAsync(CancellationToken ct)
    {
        var listResp = await raw.CustomObjects.ListClusterCustomObjectAsync(
            _gvk.Group, _gvk.Version, _gvk.PluralName, cancellationToken: ct).ConfigureAwait(false);

        // Tolerant per-item deserialization — one malformed CR must not freeze the
        // whole kind. Failed items are logged and skipped; everything else keeps flowing.
        using var doc = System.Text.Json.JsonDocument.Parse(KubernetesJson.Serialize(listResp));
        var listedKeys = new HashSet<string>();
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var element in items.EnumerateArray())
            {
                TEntity item;
                try
                {
                    item = KubernetesJson.Deserialize<TEntity>(element.GetRawText());
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Skipping malformed {Kind} item in list response", _gvk.Kind);
                    continue;
                }

                var key = Key(item);
                listedKeys.Add(key);
                _lastSeen[key] = item;
                _queue.Add(key);
            }
        }

        foreach (var deletedKey in ComputeDeletedKeys(_lastSeen.Keys, listedKeys))
            _queue.Add(deletedKey);

        return doc.RootElement.TryGetProperty("metadata", out var meta)
               && meta.TryGetProperty("resourceVersion", out var rv)
            ? rv.GetString()
            : null;
    }

    /// <summary>Keys we believed existed that a fresh list no longer contains.</summary>
    internal static List<string> ComputeDeletedKeys(IEnumerable<string> knownKeys, IReadOnlySet<string> listedKeys)
        => knownKeys.Where(k => !listedKeys.Contains(k)).ToList();

    /// <summary>
    /// Runs one watch session. Returns the resourceVersion to resume from on a clean close
    /// (server-side rotation), or null when a full relist is required (410 Gone).
    /// </summary>
    private async Task<string?> WatchFrom(string? resourceVersion, CancellationToken ct)
    {
        var lastRv = resourceVersion;
        try
        {
            using var resp = await raw.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                _gvk.Group, _gvk.Version, _gvk.PluralName,
                allowWatchBookmarks: true, timeoutSeconds: WatchTimeoutSeconds,
                watch: true, resourceVersion: resourceVersion, cancellationToken: ct).ConfigureAwait(false);

            // The watch response is a stream of newline-delimited JSON events
            // ({"type":"ADDED","object":{...}}). Read it directly: the SDK's Watch/WatchAsync
            // extensions are obsolete and hide the per-line handling we need.
            await using var stream = await resp.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string? eventType;
                System.Text.Json.JsonDocument doc;
                try
                {
                    doc = System.Text.Json.JsonDocument.Parse(line);
                }
                catch (System.Text.Json.JsonException e)
                {
                    // A poisoned line must not destroy the watch for the whole kind.
                    logger.LogError(e, "Skipping malformed {Kind} watch line", _gvk.Kind);
                    continue;
                }

                using (doc)
                {
                    eventType = doc.RootElement.GetProperty("type").GetString();
                    var objectElement = doc.RootElement.GetProperty("object");

                    switch (eventType)
                    {
                        case "ADDED" or "MODIFIED" or "DELETED":
                            TEntity item;
                            try
                            {
                                item = KubernetesJson.Deserialize<TEntity>(objectElement.GetRawText());
                            }
                            catch (Exception e)
                            {
                                logger.LogError(e, "Skipping malformed {Kind} watch event ({Type})", _gvk.Kind, eventType);
                                continue;
                            }

                            var key = Key(item);
                            _lastSeen[key] = item;
                            _queue.Add(key);
                            lastRv = item.Metadata.ResourceVersion ?? lastRv;
                            break;

                        case "BOOKMARK":
                            // Bookmarks advance the resume point without traffic.
                            if (objectElement.TryGetProperty("metadata", out var bm)
                                && bm.TryGetProperty("resourceVersion", out var bmRv))
                                lastRv = bmRv.GetString() ?? lastRv;
                            break;

                        case "ERROR":
                            // Typically a v1.Status (e.g. 410 Gone mid-stream): force a relist.
                            logger.LogWarning("{Kind} watch error event: {Status}", _gvk.Kind, objectElement.GetRawText());
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

    // ── Workers ─────────────────────────────────────────────────────────

    private async Task WorkerLoop(int index, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string key;
            try
            {
                key = await _queue.GetAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var success = false;
            try
            {
                success = await ReconcileKey(key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown — exit quietly, no error log, no failure metric, no backoff.
                _queue.Done(key, resetBackoff: false);
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Reconcile of {Kind} {Key} failed (worker {Worker})", _gvk.Kind, key, index);
            }

            _queue.Done(key, resetBackoff: success);
            if (!success)
                _queue.AddWithBackoff(key);
        }
    }

    /// <summary>Returns false when the reconcile failed (exception in the caller, or a
    /// controller returning <c>IsSuccess=false</c> — honored).</summary>
    private async Task<bool> ReconcileKey(string key, CancellationToken ct)
    {
        var (ns, name) = Split(key);

        await using var scope = services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IKubernetesClient>();
        var controller = scope.ServiceProvider.GetRequiredService<IEntityController<TEntity>>();
        var finalizer = scope.ServiceProvider.GetService<IEntityFinalizer<TEntity>>();
        var metrics = scope.ServiceProvider.GetService<IReconcileMetrics>();

        var entity = await client.GetAsync<TEntity>(name, ns, ct).ConfigureAwait(false);
        if (entity is null)
        {
            // Object is gone. Fire the post-delete hook once with the last-known state,
            // then drop all per-key bookkeeping.
            if (_lastSeen.TryRemove(key, out var last))
                await controller.DeletedAsync(last, ct).ConfigureAwait(false);
            _queue.Forget(key);
            client.ForgetStatusHashes(ns, name);
            return true;
        }

        _lastSeen[key] = entity;
        var finalizerId = finalizer?.Identifier;
        var legacyIds = finalizer?.LegacyIdentifiers ?? [];
        var finalizers = entity.Metadata.Finalizers ??= [];

        if (entity.Metadata.DeletionTimestamp is null)
        {
            // Ensure our finalizer is present (and migrate away any legacy identifier)
            // before doing real work.
            if (finalizerId is not null && (!finalizers.Contains(finalizerId) || finalizers.Any(legacyIds.Contains)))
            {
                await PatchFinalizersAsync(client, entity, fins =>
                {
                    fins.RemoveAll(legacyIds.Contains);
                    if (!fins.Contains(finalizerId)) fins.Add(finalizerId);
                }, ct).ConfigureAwait(false);
                _queue.Add(key); // re-reconcile with the finalizer set
                return true;
            }

            var sw = metrics is null ? null : System.Diagnostics.Stopwatch.StartNew();
            var ok = false;
            try
            {
                var result = await controller.ReconcileAsync(entity, ct).ConfigureAwait(false);
                // A controller returning IsSuccess=false is a real failure — log it
                // and let the worker apply backoff (the previous runtime silently ignored it).
                ok = result.IsSuccess;
                if (!ok)
                {
                    logger.LogWarning("Reconcile of {Kind} {Key} reported failure: {Error}",
                        _gvk.Kind, key, result.ErrorMessage ?? "(no message)");
                    return false;
                }

                if (result.RequeueAfter is { } requeue)
                    _queue.AddAfter(key, requeue);
                else if (defaultResync > TimeSpan.Zero)
                    // Terminal success still resyncs (jittered) so drift on owned
                    // resources is repaired without any CR change.
                    _queue.AddAfter(key, Jitter(defaultResync));
            }
            finally
            {
                if (sw is not null)
                    metrics!.RecordReconciliation(_gvk.Kind, sw.Elapsed.TotalSeconds, ok);
            }
        }
        else
        {
            // Deletion requested.
            if (finalizer is not null && finalizerId is not null
                && (finalizers.Contains(finalizerId) || finalizers.Any(legacyIds.Contains)))
            {
                var result = await finalizer.FinalizeAsync(entity, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw new InvalidOperationException(
                        $"Finalizer for {_gvk.Kind} {key} not complete: {result.ErrorMessage}");

                await PatchFinalizersAsync(client, entity,
                    fins => fins.RemoveAll(f => f == finalizerId || legacyIds.Contains(f)),
                    ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    // ── Finalizer patching ──────────────────────────────────────────────

    /// <summary>
    /// Mutates only our finalizer entries via a JSON Patch guarded by a resourceVersion test
    /// finalizers added by third parties are never clobbered or resurrected. On a
    /// 409/412 (concurrent write) the object is re-fetched and the patch retried.
    /// </summary>
    private async Task PatchFinalizersAsync(IKubernetesClient client, TEntity entity, Action<List<string>> mutate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fins = entity.Metadata.Finalizers?.ToList() ?? [];
            var before = fins.Count == 0 ? "" : string.Join("\n", fins);
            mutate(fins);
            if (string.Join("\n", fins) == before)
                return; // nothing to change

            object[] ops =
            [
                new { op = "test", path = "/metadata/resourceVersion", value = entity.Metadata.ResourceVersion },
                new { op = "add", path = "/metadata/finalizers", value = fins },
            ];
            var patch = new V1Patch(KubernetesJson.Serialize(ops), V1Patch.PatchType.JsonPatch);

            try
            {
                await raw.CustomObjects.PatchNamespacedCustomObjectAsync(
                    patch, _gvk.Group, _gvk.Version, entity.Metadata.NamespaceProperty,
                    _gvk.PluralName, entity.Metadata.Name, cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            catch (HttpOperationException e) when (
                e.Response.StatusCode is System.Net.HttpStatusCode.Conflict
                    or System.Net.HttpStatusCode.UnprocessableEntity
                && attempt < 3)
            {
                var fresh = await client.GetAsync<TEntity>(entity.Metadata.Name, entity.Metadata.NamespaceProperty, ct)
                    .ConfigureAwait(false);
                if (fresh is null)
                    return; // object vanished — nothing to patch
                entity = fresh;
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>±20% jitter to avoid synchronized resync waves after a restart.</summary>
    private static TimeSpan Jitter(TimeSpan interval)
        => interval * (0.8 + 0.4 * (System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000) / 1000.0));

    public void Dispose() => _queue.Dispose();

    private static string Key(TEntity e) => $"{e.Metadata.NamespaceProperty}/{e.Metadata.Name}";

    private static (string? Namespace, string Name) Split(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? (null, key) : (key[..slash], key[(slash + 1)..]);
    }
}
