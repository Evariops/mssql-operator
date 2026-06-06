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
    int workerCount = 2)
    : IEntityRuntime
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

    private async Task WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Initial list: seed the queue with every existing object and capture the resourceVersion.
                var listResp = await raw.CustomObjects.ListClusterCustomObjectAsync(
                    _gvk.Group, _gvk.Version, _gvk.PluralName, cancellationToken: ct).ConfigureAwait(false);
                var list = KubernetesJson.Deserialize<EntityList<TEntity>>(KubernetesJson.Serialize(listResp));
                foreach (var item in list.Items ?? [])
                {
                    _lastSeen[Key(item)] = item;
                    _queue.Add(Key(item));
                }

                var resourceVersion = list.Metadata?.ResourceVersion;
                await WatchFrom(resourceVersion, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "{Kind} watch failed; relisting in 5s", _gvk.Kind);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchFrom(string? resourceVersion, CancellationToken ct)
    {
        var resp = raw.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
            _gvk.Group, _gvk.Version, _gvk.PluralName,
            watch: true, resourceVersion: resourceVersion, cancellationToken: ct);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => closed.TrySetCanceled());

        using var watcher = resp.Watch<TEntity, object>(
            onEvent: (type, item) =>
            {
                var key = Key(item);
                switch (type)
                {
                    case WatchEventType.Added or WatchEventType.Modified:
                        _lastSeen[key] = item;
                        _queue.Add(key);
                        break;
                    case WatchEventType.Deleted:
                        _lastSeen[key] = item;
                        _queue.Add(key);
                        break;
                    case WatchEventType.Error:
                        closed.TrySetException(new InvalidOperationException("watch error event"));
                        break;
                }
            },
            onError: e => closed.TrySetException(e),
            onClosed: () => closed.TrySetResult());

        try
        {
            await closed.Task.ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            // 410 Gone: resourceVersion too old, fall through to a full relist.
        }
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
                await ReconcileKey(key, ct).ConfigureAwait(false);
                success = true;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Reconcile of {Kind} {Key} failed (worker {Worker})", _gvk.Kind, key, index);
            }
            finally
            {
                _queue.Done(key, resetBackoff: success);
                if (!success)
                    _queue.AddWithBackoff(key);
            }
        }
    }

    private async Task ReconcileKey(string key, CancellationToken ct)
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
            // Object is gone. Fire the post-delete hook once with the last-known state.
            if (_lastSeen.TryRemove(key, out var last))
                await controller.DeletedAsync(last, ct).ConfigureAwait(false);
            return;
        }

        _lastSeen[key] = entity;
        var finalizerId = finalizer?.Identifier;
        var finalizers = entity.Metadata.Finalizers ??= [];

        if (entity.Metadata.DeletionTimestamp is null)
        {
            // Ensure our finalizer is present before doing real work.
            if (finalizerId is not null && !finalizers.Contains(finalizerId))
            {
                await PatchFinalizers(entity, [.. finalizers, finalizerId], ct).ConfigureAwait(false);
                _queue.Add(key); // re-reconcile with the finalizer set
                return;
            }

            var sw = metrics is null ? null : System.Diagnostics.Stopwatch.StartNew();
            var ok = false;
            try
            {
                var result = await controller.ReconcileAsync(entity, ct).ConfigureAwait(false);
                ok = true;
                if (result.RequeueAfter is { } requeue)
                    _queue.AddAfter(key, requeue);
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
            if (finalizer is not null && finalizerId is not null && finalizers.Contains(finalizerId))
            {
                var result = await finalizer.FinalizeAsync(entity, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw new InvalidOperationException(
                        $"Finalizer for {_gvk.Kind} {key} not complete: {result.ErrorMessage}");

                await PatchFinalizers(entity, finalizers.Where(f => f != finalizerId).ToList(), ct).ConfigureAwait(false);
            }
        }
    }

    // ── Finalizer patching ──────────────────────────────────────────────

    private async Task PatchFinalizers(TEntity entity, IList<string> finalizers, CancellationToken ct)
    {
        var patch = new V1Patch(
            KubernetesJson.Serialize(new { metadata = new { finalizers } }),
            V1Patch.PatchType.MergePatch);

        await raw.CustomObjects.PatchNamespacedCustomObjectAsync(
            patch, _gvk.Group, _gvk.Version, entity.Metadata.NamespaceProperty,
            _gvk.PluralName, entity.Metadata.Name, cancellationToken: ct).ConfigureAwait(false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string Key(TEntity e) => $"{e.Metadata.NamespaceProperty}/{e.Metadata.Name}";

    private static (string? Namespace, string Name) Split(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? (null, key) : (key[..slash], key[(slash + 1)..]);
    }
}
