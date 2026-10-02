using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using k8s;
using k8s.Models;
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
                c.ListJson,
                c.WatchSession,
                items => Seed(c.Kind, items),
                (type, obj) => Route(c.Kind, type, obj),
                _time,
                stopIfResourceAbsent: true)))
            .ToList();
        _watchers = watchers;

        // Feed the zombie gauge: seconds since each child watch last saw activity.
        // A watcher disabled because its type is not served (e.g. Cilium absent) is not reported.
        metrics.SetOwnedWatchAgeProvider(() => watchers.Where(w => !w.Watcher.Stopped).Select(w =>
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
        // A watcher disabled because its type is not served (404) is excluded — its absence is
        // not a zombie stream.
        return watchers.All(w => w.Watcher.Stopped
            || (now - w.Watcher.LastActivity).TotalSeconds <= 2.0 * w.Watcher.WatchTimeoutSeconds);
    }

    private List<ChildKind> BuildChildKinds()
    {
        const string selector = OperatorLabels.ManagedBySelector;
        const int timeout = 300;

        return
        [
            new("StatefulSet",
                c => ListPager.CollectAsync(async (next, t) => await raw.AppsV1.ListStatefulSetForAllNamespacesAsync(
                    labelSelector: selector, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.AppsV1.WatchListStatefulSetForAllNamespacesAsync(
                        labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError("StatefulSet"), cancellationToken: c),
                    "StatefulSet", logger, onAct, emit, c)),
            new("Service",
                c => ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListServiceForAllNamespacesAsync(
                    labelSelector: selector, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.CoreV1.WatchListServiceForAllNamespacesAsync(
                        labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError("Service"), cancellationToken: c),
                    "Service", logger, onAct, emit, c)),
            new("ConfigMap",
                c => ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListConfigMapForAllNamespacesAsync(
                    labelSelector: selector, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.CoreV1.WatchListConfigMapForAllNamespacesAsync(
                        labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError("ConfigMap"), cancellationToken: c),
                    "ConfigMap", logger, onAct, emit, c)),
            new("PodDisruptionBudget",
                c => ListPager.CollectAsync(async (next, t) => await raw.PolicyV1.ListPodDisruptionBudgetForAllNamespacesAsync(
                    labelSelector: selector, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.PolicyV1.WatchListPodDisruptionBudgetForAllNamespacesAsync(
                        labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError("PodDisruptionBudget"), cancellationToken: c),
                    "PodDisruptionBudget", logger, onAct, emit, c)),
            // CiliumNetworkPolicy is a CRD: watched through the dynamic CustomObjects endpoint. It is
            // optional — if Cilium is not installed the 404 disables just this watcher (stopIfResourceAbsent).
            new("CiliumNetworkPolicy",
                c => ListPager.CollectAsync((next, t) => raw.CustomObjects.ListClusterCustomObjectAsync(
                    CiliumGvk.Group, CiliumGvk.Version, CiliumGvk.PluralName,
                    labelSelector: selector, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.CustomObjects.WatchListClusterCustomObjectAsync(
                        CiliumGvk.Group, CiliumGvk.Version, CiliumGvk.PluralName,
                        labelSelector: selector, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError("CiliumNetworkPolicy"), cancellationToken: c),
                    "CiliumNetworkPolicy", logger, onAct, emit, c)),
        ];
    }

    private Action<Exception> WatchError(string childKind)
        => e => logger.LogDebug(e, "{ChildKind} (owned) watch stream error", childKind);

    /// <summary>
    /// Seeds the generation map from a relist.
    ///
    /// The first relist (startup) only seeds: the parent runtimes reconcile every CR on their own
    /// startup relist anyway, so enqueuing here would just cause a storm.
    ///
    /// Every LATER relist — the one that follows a 410 Gone or a dropped stream — must
    /// DIFF. Events missed during the outage are exactly the ones that matter: a child deleted while
    /// the watch was down (someone removed the StatefulSet, a GC pass took the Service) never
    /// reappeared in any event, and the parent was never told; the drift then survived until a human
    /// noticed. Keys that vanished, and generations that moved, now enqueue their parent.
    /// </summary>
    internal void Seed(string childKind, IReadOnlyList<JsonElement> items)
    {
        var seen = new HashSet<string>();
        var firstSeed = _seeded.TryAdd(childKind, 0);

        foreach (var item in items)
        {
            if (!TryChildKey(childKind, item, out var key))
                continue;

            seen.Add(key);
            var generation = GenerationOf(item);

            // Remember the parent of EVERY child seen in EVERY relist, not just the
            // first one. A child discovered on a later relist (created while the watch was down) used
            // to take the Enqueue branch only — nothing wrote its mapping — so if it then disappeared
            // during a SUBSEQUENT outage, the deletion had no parent to route to and the drift went
            // unrepaired, which is precisely the hole this watch set out to close.
            if (MapToParent(item) is { } mapping)
                _parentOf[key] = mapping;

            if (!firstSeed && (!_lastGeneration.TryGetValue(key, out var previous) || previous != generation))
            {
                // Created or modified while we were not watching.
                Enqueue(childKind, item);
            }

            _lastGeneration[key] = generation;
        }

        if (firstSeed)
            return;

        // Children that disappeared while the watch was down: the parent must repair the drift.
        foreach (var key in _lastGeneration.Keys
                     .Where(k => k.StartsWith(childKind + "/", StringComparison.Ordinal) && !seen.Contains(k))
                     .ToList())
        {
            _lastGeneration.TryRemove(key, out _);
            if (_parentOf.TryRemove(key, out var parent))
            {
                EnqueueParent(parent.ParentKind, parent.ParentKey, childKind);
                logger.LogWarning(
                    "Owned {ChildKind} {Key} disappeared while the watch was down; reconciling its parent {ParentKind} {ParentKey}",
                    childKind, key, parent.ParentKind, parent.ParentKey);
            }
        }
    }

    /// <summary>Child kinds whose first relist has already been consumed. Concurrent:
    /// the five child watchers seed it from their own tasks at startup.</summary>
    private readonly ConcurrentDictionary<string, byte> _seeded = new();

    /// <summary>Parent of each known child: a DELETE observed only as an absence from a
    /// relist carries no ownerReference anymore, so the mapping has to be remembered — and dropped
    /// once the child is gone, or it grows with every child ever deleted.</summary>
    private readonly ConcurrentDictionary<string, (string ParentKind, string ParentKey)> _parentOf = new();

    private void EnqueueParent(string parentKind, string parentKey, string childKind)
    {
        if (!_runtimesByKind.TryGetValue(parentKind, out var runtime))
            return;
        runtime.Enqueue(parentKey);
        metrics.RecordOwnedWatchEnqueue(childKind, parentKind);
    }

    internal void Route(string childKind, string eventType, JsonElement obj)
    {
        if (!TryChildKey(childKind, obj, out var childKey))
            return;

        if (eventType == "DELETED")
        {
            _lastGeneration.TryRemove(childKey, out _);
            Enqueue(childKind, obj, rememberParent: false);
            _parentOf.TryRemove(childKey, out _);
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

    private void Enqueue(string childKind, JsonElement obj, bool rememberParent = true)
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

        // Remember the mapping: once the child is gone, its ownerReference is gone with it, and a
        // deletion noticed only through a relist diff would have nothing left to route.
        if (rememberParent && TryChildKey(childKind, obj, out var childKey))
            _parentOf[childKey] = (parentKind, parentKey);

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

    private sealed record ChildKind(
        string Kind,
        Func<CancellationToken, Task<string>> ListJson,
        Func<string?, Action, Action<string, JsonElement>, CancellationToken, Task<string?>> WatchSession);
}
