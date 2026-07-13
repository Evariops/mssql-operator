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
    TimeSpan defaultResync = default,
    TimeSpan reconcileTimeout = default)
    : IEntityRuntime, IDisposable
    where TEntity : class, IKubernetesObject<V1ObjectMeta>, new()
{
    private readonly GroupVersionKind _gvk = GroupVersionKind.Of<TEntity>();
    private readonly WorkQueue _queue = new();
    private readonly ConcurrentDictionary<string, TEntity> _lastSeen = new();

    public string EntityKind => _gvk.Kind;

    /// <summary>
    /// Upper bound on a single reconcile pass. Nothing bounded it: one SQL call without
    /// a timeout — a full backup with CommandTimeout=0, a connection to a dead node retried for 2.6
    /// minutes — held one of the (2) workers of a kind indefinitely. Two such passes starved every
    /// other resource of that kind, including the health checks that detect a dead primary, exactly
    /// during an incident.
    /// </summary>
    private readonly TimeSpan _reconcileTimeout = reconcileTimeout > TimeSpan.Zero
        ? reconcileTimeout
        : TimeSpan.FromMinutes(10);

    /// <summary>Server-side watch rotation: the API server closes the stream after
    /// this many seconds, so a half-dead connection can never become a silent zombie.</summary>
    private const int WatchTimeoutSeconds = 300;

    public async Task RunAsync(CancellationToken ct)
    {
        // The CR watch reuses the shared streaming mechanism (factored out for the owned-resource watch).
        var watcher = new ResourceWatcher(_gvk.Kind, logger, ListJsonAsync, WatchSessionAsync, SeedFromRelist, EnqueueFromEvent,
            watchTimeoutSeconds: WatchTimeoutSeconds);
        var workers = Enumerable.Range(0, workerCount).Select(i => WorkerLoop(i, ct)).ToArray();
        await Task.WhenAll(workers.Append(watcher.RunAsync(ct))).ConfigureAwait(false);
    }

    // ── Watch (list → watch → relist), via the shared ResourceWatcher ────

    // The relist uses the deserialized list object (re-serialized), NOT the raw HTTP stream: the
    // non-watch list call consumes its response stream to build the typed body, so reading it again
    // would throw "stream already consumed".
    private async Task<string> ListJsonAsync(CancellationToken ct)
        => KubernetesJson.Serialize(await raw.CustomObjects.ListClusterCustomObjectAsync(
            _gvk.Group, _gvk.Version, _gvk.PluralName, cancellationToken: ct).ConfigureAwait(false));

    // The CR watch streams raw objects; K8sWatchSession re-serializes each to JSON for seeding.
    private Task<string?> WatchSessionAsync(string? resourceVersion, Action onActivity, Action<string, System.Text.Json.JsonElement> emit, CancellationToken ct)
        => K8sWatchSession.RunAsync(
            raw.CustomObjects.WatchListClusterCustomObjectAsync(
                _gvk.Group, _gvk.Version, _gvk.PluralName,
                allowWatchBookmarks: true, resourceVersion: resourceVersion, timeoutSeconds: WatchTimeoutSeconds,
                onError: e => logger.LogDebug(e, "{Kind} watch stream error", _gvk.Kind), cancellationToken: ct),
            _gvk.Kind, logger, onActivity, emit, ct);

    /// <summary>
    /// Seeds the queue from a full relist and detects deletions missed while the watch was down
    /// (known keys absent from the list are enqueued — the worker sees the 404 and
    /// fires DeletedAsync + purges bookkeeping). One malformed CR is skipped, not fatal.
    /// </summary>
    private void SeedFromRelist(IReadOnlyList<System.Text.Json.JsonElement> items)
    {
        var listedKeys = new HashSet<string>();
        foreach (var element in items)
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

        foreach (var deletedKey in ComputeDeletedKeys(_lastSeen.Keys, listedKeys))
            _queue.Add(deletedKey);
    }

    /// <summary>Keys we believed existed that a fresh list no longer contains.</summary>
    internal static List<string> ComputeDeletedKeys(IEnumerable<string> knownKeys, IReadOnlySet<string> listedKeys)
        => knownKeys.Where(k => !listedKeys.Contains(k)).ToList();

    // Deserialization failures here are tolerated by ResourceWatcher (logged + skipped per-event).
    private void EnqueueFromEvent(string eventType, System.Text.Json.JsonElement objectElement)
    {
        var item = KubernetesJson.Deserialize<TEntity>(objectElement.GetRawText());
        var key = Key(item);
        _lastSeen[key] = item;
        _queue.Add(key);
    }

    /// <summary>
    /// Enqueues a parent key (<c>{ns}/{name}</c>) for reconciliation from outside the CR watch —
    /// used by the owned-resource watch to route a child event to its parent runtime. The
    /// <c>WorkQueue</c> dedups, so a storm of child events on one parent collapses to one reconcile.
    /// </summary>
    public void Enqueue(string key) => _queue.Add(key);

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
                // Bound the pass. A worker held hostage by one resource is a worker the
                // whole kind no longer has.
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pass.CancelAfter(_reconcileTimeout);
                try
                {
                    success = await ReconcileKey(key, pass.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && pass.IsCancellationRequested)
                {
                    logger.LogError("Reconcile of {Kind} {Key} exceeded the {Timeout}s budget and was cancelled (worker {Worker})",
                        _gvk.Kind, key, _reconcileTimeout.TotalSeconds, index);
                }
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

            // Publish saturation. The queue depth and the age of the oldest waiting key
            // are what tell an operator that its availability groups are no longer being checked.
            if (services.GetService<IReconcileMetrics>() is { } queueMetrics)
                queueMetrics.SetQueueStats(_gvk.Kind, _queue.Depth, _queue.OldestAgeSeconds);
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
            // Ensure our finalizer is present (and migrate away any legacy identifier) before doing
            // real work.
            //
            // This runs BEFORE the pause check. Attaching the finalizer is not a
            // spec→reality convergence — it is what makes an ordered deletion possible at all. When the
            // pause check came first, a CR created with reconcile-paused=true never got a finalizer, so
            // deleting it skipped the very guard rails the finalizer exists for (an availability group
            // could be deleted with its databases still in it, StatefulSet and key secrets GC'd
            // underneath them).
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

            // Operational pause. While reconcile-paused=true is set, skip the spec→reality
            // convergence so an operator can do manual surgery (split-brain recovery, SMK restore,
            // replica re-provision) without the operator re-applying labels/routing/REQUIRED_SYNC
            // underneath. Deletion is deliberately NOT paused — the else branch below still runs
            // finalizers, so a paused resource can always be deleted.
            if (IsPaused(entity))
            {
                logger.LogInformation("Reconcile of {Kind} {Key} skipped: {Annotation}=true (operational pause)",
                    _gvk.Kind, key, OperatorLabels.PausedAnnotation);
                // A paused resource used to keep advertising its last phase — an AG could
                // show Running/Ready=True while no health check, failover or rolling update was running
                // at all. Say it out loud, in the status and in an event.
                await MarkPausedAsync(client, entity, key, ct).ConfigureAwait(false);
                _queue.AddAfter(key, PausedRequeueInterval);
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

    /// <summary>Re-check cadence while a CR is paused, so it resumes promptly once un-paused.</summary>
    private static readonly TimeSpan PausedRequeueInterval = TimeSpan.FromSeconds(30);

    private static bool IsPaused(TEntity entity) =>
        entity.Metadata.Annotations?.TryGetValue(OperatorLabels.PausedAnnotation, out var v) == true
        && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Surfaces the operational pause in the resource's own status: a
    /// <c>ReconcilePaused</c> condition and Ready=False, plus one Warning event. Without it, a paused
    /// availability group kept reporting Running/Ready=True while health checks, failover and rolling
    /// updates were all switched off — the monitoring said everything was fine.
    /// </summary>
    private async Task MarkPausedAsync(IKubernetesClient client, TEntity entity, string key, CancellationToken ct)
    {
        const string message =
            "Reconciliation is paused by the mssql-operator.evariops.com/reconcile-paused annotation: " +
            "health checks, failover and rolling updates are NOT running for this resource.";

        try
        {
            // Conditions live on the status of every operator CR, but the runtime is generic — reach
            // them through the shared shape rather than a per-kind cast.
            var statusProperty = typeof(TEntity).GetProperty("Status");
            if (statusProperty is null)
                return;

            var status = statusProperty.GetValue(entity);
            if (status is null)
            {
                // A CR paused before its first successful reconcile has no status object
                // yet — it used to silently show no ReconcilePaused condition at all, which is exactly
                // the "monitoring says everything is fine" failure this condition exists to prevent. Create it.
                if (!statusProperty.CanWrite || statusProperty.PropertyType.GetConstructor(Type.EmptyTypes) is null)
                    return;
                status = Activator.CreateInstance(statusProperty.PropertyType);
                if (status is null)
                    return;
                statusProperty.SetValue(entity, status);
            }

            var conditionsProperty = status.GetType().GetProperty("Conditions");
            if (conditionsProperty?.GetValue(status) is not List<Entities.Common.ConditionStatus> conditions)
            {
                if (conditionsProperty is null || conditionsProperty.PropertyType != typeof(List<Entities.Common.ConditionStatus>))
                    return;
                conditions = [];
                conditionsProperty.SetValue(status, conditions);
            }

            var before = conditions.FirstOrDefault(c => c.Type == "ReconcilePaused")?.Status;
            ConditionOps.Set(conditions, "ReconcilePaused", "True", "OperationalPause", message);
            ConditionOps.Set(conditions, "Ready", "False", "ReconcilePaused", message);

            if (before != "True")
            {
                await client.UpdateStatusAsync(entity, ct).ConfigureAwait(false);
                logger.LogWarning("{Kind} {Key} is paused: {Message}", _gvk.Kind, key, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not mark {Kind} {Key} as paused in its status", _gvk.Kind, key);
        }
    }

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
