using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// A shared multiplexer that watches the operator-managed child resources (StatefulSet,
/// Service, ConfigMap, PodDisruptionBudget, CiliumNetworkPolicy) cluster-wide and, on a change,
/// re-enqueues the owning CR for reconciliation — so drift (e.g. <c>kubectl delete statefulset</c>)
/// is repaired in seconds instead of waiting for the periodic resync. One watch per child type
/// (not per parent runtime) avoids duplicate watches on types owned by both SqlInstance and
/// SqlAvailabilityGroup. Secrets are deliberately excluded (decision 2: they mutate rarely and a
/// cluster-wide Secret watch would widen the cache's exposure).
/// </summary>
public sealed class OwnedResourceWatcher(
    IEnumerable<IEntityRuntime> runtimes,
    IKubernetes raw,
    OperatorMetrics metrics,
    ILogger<OwnedResourceWatcher> logger,
    TimeProvider? time = null)
{
    private readonly IReadOnlyDictionary<string, IEntityRuntime> _runtimesByKind =
        runtimes.ToDictionary(r => r.EntityKind, StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Last-seen metadata.generation per child key, to ignore status-only churn (decision 3): a
    // status update (e.g. StatefulSet.status.readyReplicas) never bumps generation, so it is not
    // re-enqueued; a missing generation (e.g. ConfigMap has none) treats every change as real.
    private readonly ConcurrentDictionary<string, long?> _lastGeneration = new();

    private static readonly GroupVersionKind CiliumGvk = GroupVersionKind.Of<GenericCiliumNetworkPolicy>();

    // Published once RunAsync starts (leader only); null on a non-leader replica where the watch
    // never runs. The readiness check reads it to flag a zombie watch.
    private volatile IReadOnlyList<(string Kind, ResourceWatcher Watcher)>? _watchers;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_runtimesByKind.Count == 0)
            return;

        var watchers = BuildChildKinds()
            .Select(c => (c.Kind, Watcher: new ResourceWatcher(
                $"{c.Kind} (owned)", logger,
                c.Open,
                items => Seed(c.Kind, items),
                (type, obj) => Route(c.Kind, type, obj),
                _time)))
            .ToList();
        _watchers = watchers;

        // Feed the zombie gauge: seconds since each child watch last saw activity.
        metrics.SetOwnedWatchAgeProvider(() => watchers.Select(w =>
            new Measurement<double>(
                Math.Max(0, (_time.GetUtcNow() - w.Watcher.LastActivity).TotalSeconds),
                new KeyValuePair<string, object?>("child_kind", w.Kind))));

        logger.LogInformation("Owned-resource watch starting for {Count} child kinds: {Kinds}",
            watchers.Count, string.Join(", ", watchers.Select(w => w.Kind)));

        await Task.WhenAll(watchers.Select(w => w.Watcher.RunAsync(ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// True unless a child watch has gone silent for longer than 2× its rotation timeout — a
    /// zombie stream. Returns true when the watch is not running (e.g. on a
    /// non-leader replica), since there is nothing to flag there.
    /// </summary>
    public bool IsHealthy()
    {
        var watchers = _watchers;
        if (watchers is null)
            return true;
        var now = _time.GetUtcNow();
        return watchers.All(w =>
            (now - w.Watcher.LastActivity).TotalSeconds <= 2.0 * w.Watcher.WatchTimeoutSeconds);
    }

    private List<ChildKind> BuildChildKinds()
    {
        const string selector = OperatorLabels.ManagedBySelector;

        return
        [
            new("StatefulSet", async (watch, rv, c) => await raw.AppsV1.ListStatefulSetForAllNamespacesWithHttpMessagesAsync(
                labelSelector: selector, allowWatchBookmarks: watch ? true : null, watch: watch ? true : null,
                timeoutSeconds: watch ? 300 : null, resourceVersion: rv, cancellationToken: c).ConfigureAwait(false)),
            new("Service", async (watch, rv, c) => await raw.CoreV1.ListServiceForAllNamespacesWithHttpMessagesAsync(
                labelSelector: selector, allowWatchBookmarks: watch ? true : null, watch: watch ? true : null,
                timeoutSeconds: watch ? 300 : null, resourceVersion: rv, cancellationToken: c).ConfigureAwait(false)),
            new("ConfigMap", async (watch, rv, c) => await raw.CoreV1.ListConfigMapForAllNamespacesWithHttpMessagesAsync(
                labelSelector: selector, allowWatchBookmarks: watch ? true : null, watch: watch ? true : null,
                timeoutSeconds: watch ? 300 : null, resourceVersion: rv, cancellationToken: c).ConfigureAwait(false)),
            new("PodDisruptionBudget", async (watch, rv, c) => await raw.PolicyV1.ListPodDisruptionBudgetForAllNamespacesWithHttpMessagesAsync(
                labelSelector: selector, allowWatchBookmarks: watch ? true : null, watch: watch ? true : null,
                timeoutSeconds: watch ? 300 : null, resourceVersion: rv, cancellationToken: c).ConfigureAwait(false)),
            // CiliumNetworkPolicy is a CRD: watched through the dynamic CustomObjects endpoint.
            new("CiliumNetworkPolicy", async (watch, rv, c) => await raw.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                CiliumGvk.Group, CiliumGvk.Version, CiliumGvk.PluralName,
                labelSelector: selector, allowWatchBookmarks: watch ? true : null, watch: watch ? true : null,
                timeoutSeconds: watch ? 300 : null, resourceVersion: rv, cancellationToken: c).ConfigureAwait(false)),
        ];
    }

    // Seed the generation map from the relist WITHOUT enqueuing: the parent runtimes already
    // reconcile every CR on their own startup relist, so seeding-only avoids a startup storm.
    internal void Seed(string childKind, IReadOnlyList<JsonElement> items)
    {
        foreach (var item in items)
        {
            if (TryChildKey(childKind, item, out var key))
                _lastGeneration[key] = GenerationOf(item);
        }
    }

    internal void Route(string childKind, string eventType, JsonElement obj)
    {
        if (!TryChildKey(childKind, obj, out var childKey))
            return;

        if (eventType == "DELETED")
        {
            _lastGeneration.TryRemove(childKey, out _);
            Enqueue(childKind, obj);
            return;
        }

        // ADDED/MODIFIED: enqueue only on a real spec/metadata change (decision 3). A child created
        // after the seed (key unknown) enqueues once; an unchanged generation is status-only churn.
        var generation = GenerationOf(obj);
        var known = _lastGeneration.TryGetValue(childKey, out var previous);
        _lastGeneration[childKey] = generation;
        if (known && generation is not null && previous == generation)
            return;

        Enqueue(childKind, obj);
    }

    private void Enqueue(string childKind, JsonElement obj)
    {
        var mapping = MapToParent(obj);
        if (mapping is null)
        {
            // A managed-by-labelled child with no usable controller ownerReference is a labelling
            // leak: it would be watched but never routed. Surface it loudly.
            logger.LogError("Owned {ChildKind} event has no controller ownerReference; cannot route to a parent", childKind);
            return;
        }

        var (parentKind, parentKey) = mapping.Value;
        if (!_runtimesByKind.TryGetValue(parentKind, out var runtime))
            return; // owned by a kind this operator does not run; ignore

        runtime.Enqueue(parentKey);
        metrics.RecordOwnedWatchEnqueue(childKind, parentKind);
        logger.LogDebug("Owned {ChildKind} change → reconcile {ParentKind} {ParentKey}", childKind, parentKind, parentKey);
    }

    /// <summary>
    /// Maps a child object to its owning CR: returns <c>(parentKind, "{ns}/{parentName}")</c> from
    /// the <c>controller=true</c> owner reference. Pure and testable. Returns null
    /// when there is no controller owner reference.
    /// </summary>
    internal static (string ParentKind, string ParentKey)? MapToParent(JsonElement obj)
    {
        if (!obj.TryGetProperty("metadata", out var meta))
            return null;

        var ns = meta.TryGetProperty("namespace", out var n) ? n.GetString() : null;
        if (!meta.TryGetProperty("ownerReferences", out var owners) || owners.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var owner in owners.EnumerateArray())
        {
            var isController = owner.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.True;
            if (!isController)
                continue;

            var kind = owner.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var name = owner.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
                return null;

            return (kind, ns is null ? name : $"{ns}/{name}");
        }

        return null;
    }

    private static bool TryChildKey(string childKind, JsonElement obj, out string key)
    {
        key = string.Empty;
        if (!obj.TryGetProperty("metadata", out var meta))
            return false;
        var name = meta.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name))
            return false;
        var ns = meta.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() : null;
        key = $"{childKind}/{ns}/{name}";
        return true;
    }

    private static long? GenerationOf(JsonElement obj)
        => obj.TryGetProperty("metadata", out var m)
           && m.TryGetProperty("generation", out var g)
           && g.ValueKind == JsonValueKind.Number
            ? g.GetInt64()
            : null;

    private sealed record ChildKind(string Kind, Func<bool, string?, CancellationToken, Task<HttpOperationResponse>> Open);
}
