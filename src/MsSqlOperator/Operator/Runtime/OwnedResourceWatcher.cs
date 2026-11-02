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
/// SqlAvailabilityGroup. Secrets are watched only when labelled with their owner, and only
/// their metadata is kept — decision 2 kept them out so that no Secret data sits in the cache.
/// </summary>
public sealed class OwnedResourceWatcher(
    IEnumerable<IEntityRuntime> runtimes,
    IKubernetes raw,
    OperatorMetrics metrics,
    ILogger<OwnedResourceWatcher> logger,
    TimeProvider? time = null,
    MsSqlOperator.Services.Maintenance.MaintenanceSignals? signals = null)
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
        // The nodes, for the signals of a coming stop (cordon, disruption taints, shutdown).
        // Only the facts that can announce a stop are kept, and a node change reconciles the owners of
        // the SQL pods it runs — nothing else.
        if (signals is not null)
            watchers.Add((NodeKind, new ResourceWatcher($"{NodeKind} (maintenance)", logger,
                c => ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListNodeAsync(
                    continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.CoreV1.WatchListNodeAsync(allowWatchBookmarks: true, timeoutSeconds: 300,
                        resourceVersion: rv, onError: WatchError(NodeKind), cancellationToken: c),
                    NodeKind, logger, onAct, emit, c),
                SeedNodes, RouteNode, _time, stopIfForbidden: true)));
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
            // The SQL pods — a recreation, a deletion, a change of readiness reconciles the
            // owner at once instead of at its next periodic pass.
            PodKind(PodOfInstance, OperatorLabels.Instance),
            PodKind(PodOfAg, OperatorLabels.Ag),
            // The children that carry no ownerReference by design — key-material Secrets
            // (they outlive their CR) and the PVCs of the StatefulSet controller — named by their
            // owner-kind/owner-uid labels. The deletion of a key Secret is seen at once. Secrets are
            // reduced to their metadata before anything else sees them: no Secret data is kept.
            new(LabelledSecret,
                c => ListPager.CollectAsync(async (next, t) => MetadataOnly(await raw.CoreV1.ListSecretForAllNamespacesAsync(
                    labelSelector: OperatorLabels.OwnerKind, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t)
                    .ConfigureAwait(false)), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    MetadataOnly(raw.CoreV1.WatchListSecretForAllNamespacesAsync(
                        labelSelector: OperatorLabels.OwnerKind, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError(LabelledSecret), cancellationToken: c), c),
                    LabelledSecret, logger, onAct, emit, c)),
            new(LabelledPvc,
                c => ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                    labelSelector: OperatorLabels.OwnerKind, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t)
                    .ConfigureAwait(false), c),
                (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                    raw.CoreV1.WatchListPersistentVolumeClaimForAllNamespacesAsync(
                        labelSelector: OperatorLabels.OwnerKind, allowWatchBookmarks: true, timeoutSeconds: timeout,
                        resourceVersion: rv, onError: WatchError(LabelledPvc), cancellationToken: c),
                    LabelledPvc, logger, onAct, emit, c)),
        ];
    }

    internal const string NodeKind = "Node";

    /// <summary>A relist of the nodes. The first only fills the signals; a later one (after
    /// a dropped stream) reconciles the owners of the pods of every node whose signals moved.</summary>
    internal void SeedNodes(IReadOnlyList<JsonElement> items)
    {
        if (signals is null)
            return;
        var first = !signals.IsNodesSeeded;
        foreach (var node in items)
        {
            if (signals.ObserveNode(node) && !first && signals.NodeName(node) is { } name)
                EnqueueOwnersOn(name);
        }
        signals.MarkNodesSeeded();
    }

    internal void RouteNode(string eventType, JsonElement node)
    {
        if (signals is null || signals.NodeName(node) is not { } name)
            return;
        var changed = eventType == "DELETED" ? signals.ForgetNode(node) : signals.ObserveNode(node);
        if (changed)
            EnqueueOwnersOn(name);
    }

    private void EnqueueOwnersOn(string node)
    {
        foreach (var (kind, key) in signals!.OwnersOn(node))
        {
            EnqueueParent(kind, key, NodeKind);
            logger.LogDebug("Node {Node} signals changed → reconcile {ParentKind} {ParentKey}", node, kind, key);
        }
    }

    internal const string LabelledSecret = "Secret (labelled)";
    internal const string LabelledPvc = "PersistentVolumeClaim (labelled)";

    /// <summary>The metadata of a Secret list, without data — nor the last-applied annotation, which
    /// can hold it.</summary>
    private static SecretMetadataList MetadataOnly(V1SecretList list)
        => new(list.Metadata, list.Items.Select(s => MetadataOf(s.Metadata)).ToList());

    private static async IAsyncEnumerable<(WatchEventType Type, SecretMetadata Item)> MetadataOnly(
        IAsyncEnumerable<(WatchEventType Type, V1Secret Item)> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var (type, secret) in events.WithCancellation(ct).ConfigureAwait(false))
            yield return (type, MetadataOf(secret.Metadata));
    }

    private static SecretMetadata MetadataOf(V1ObjectMeta? metadata) => new(new V1ObjectMeta
    {
        Name = metadata?.Name,
        NamespaceProperty = metadata?.NamespaceProperty,
        Uid = metadata?.Uid,
        ResourceVersion = metadata?.ResourceVersion,
        Labels = metadata?.Labels,
        DeletionTimestamp = metadata?.DeletionTimestamp,
    });

    /// <summary>A Secret as the watcher sees it: its metadata only.</summary>
    private sealed record SecretMetadata(V1ObjectMeta Metadata);

    private sealed record SecretMetadataList(V1ListMeta Metadata, List<SecretMetadata> Items);

    private const string PodOfInstance = "Pod (instance)";
    private const string PodOfAg = "Pod (ag)";

    private ChildKind PodKind(string kind, string label)
        => new(kind,
            c => ListPager.CollectAsync(async (next, t) => await raw.CoreV1.ListPodForAllNamespacesAsync(
                labelSelector: label, continueParameter: next, limit: ListPager.PageSize, cancellationToken: t).ConfigureAwait(false), c),
            (rv, onAct, emit, c) => K8sWatchSession.RunAsync(
                raw.CoreV1.WatchListPodForAllNamespacesAsync(
                    labelSelector: label, allowWatchBookmarks: true, timeoutSeconds: 300,
                    resourceVersion: rv, onError: WatchError(kind), cancellationToken: c),
                kind, logger, onAct, emit, c));

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
            var generation = SignatureOf(item);

            // Remember the parent of EVERY child seen in EVERY relist, not just the
            // first one. A child discovered on a later relist (created while the watch was down) used
            // to take the Enqueue branch only — nothing wrote its mapping — so if it then disappeared
            // during a SUBSEQUENT outage, the deletion had no parent to route to and the drift went
            // unrepaired, which is precisely the hole this watch set out to close.
            if (ParentOf(childKind, item) is { } mapping)
                _parentOf[key] = mapping;

            if (!firstSeed && (!_lastGeneration.TryGetValue(key, out var previous) || previous != generation))
            {
                // Created or modified while we were not watching.
                Enqueue(childKind, item);
            }

            _lastGeneration[key] = generation;
        }

        if (signals is not null && childKind is PodOfInstance or PodOfAg)
        {
            var pods = items.Select(i => (Item: i, Parent: MapToParent(childKind, i)))
                .Where(p => p.Parent is not null)
                .Select(p => (p.Parent!.Value.ParentKey, p.Item))
                .ToList();
            signals.ReplacePods(childKind == PodOfInstance ? "SqlInstance" : "SqlAvailabilityGroup", pods);
            signals.MarkPodsSeeded(childKind);
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

        if (signals is not null && childKind is PodOfInstance or PodOfAg)
        {
            if (eventType == "DELETED")
                signals.ForgetPod(obj);
            else if (MapToParent(childKind, obj) is { } owner)
                signals.ObservePod(owner.ParentKind, owner.ParentKey, obj);
        }

        if (eventType == "DELETED")
        {
            // The owner of a deleted key Secret rebuilds it on this very reconcile.
            if (childKind == LabelledSecret && obj.TryGetProperty("metadata", out var deletedMeta)
                && deletedMeta.TryGetProperty("labels", out var deletedLabels)
                && deletedLabels.TryGetProperty(OperatorLabels.OwnerUid, out var ownerUid) && ownerUid.GetString() is { Length: > 0 } uid)
                InfrastructureDue.Mark(uid);
            _lastGeneration.TryRemove(childKey, out _);
            Enqueue(childKind, obj, rememberParent: false);
            _parentOf.TryRemove(childKey, out _);
            return;
        }

        // ADDED/MODIFIED: enqueue only on a real spec/metadata change (decision 3). A child created
        // after the seed (key unknown) enqueues once; an unchanged generation is status-only churn.
        var generation = SignatureOf(obj);
        var known = _lastGeneration.TryGetValue(childKey, out var previous);
        _lastGeneration[childKey] = generation;
        if (known && generation is not null && previous == generation)
            return;

        Enqueue(childKind, obj);
    }

    private void Enqueue(string childKind, JsonElement obj, bool rememberParent = true)
    {
        var mapping = ParentOf(childKind, obj);
        if (mapping is null && childKind is LabelledSecret or LabelledPvc)
        {
            // The owner is gone, or not yet seen by its runtime (a key Secret outlives its CR by
            // design): nothing to reconcile.
            logger.LogDebug("{ChildKind} change with no known owner; ignored", childKind);
            return;
        }
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

    /// <summary>The parent of a child labelled with its owner's kind and UID (key Secrets,
    /// PVCs) is the resource of that UID the owner kind's runtime knows; the others are mapped from
    /// their labels or ownerReference (<see cref="MapToParent(string, JsonElement)"/>).</summary>
    private (string ParentKind, string ParentKey)? ParentOf(string childKind, JsonElement obj)
    {
        if (childKind is not (LabelledSecret or LabelledPvc))
            return MapToParent(childKind, obj);
        if (!obj.TryGetProperty("metadata", out var meta) || !meta.TryGetProperty("labels", out var labels)
            || !labels.TryGetProperty(OperatorLabels.OwnerKind, out var k) || k.GetString() is not { Length: > 0 } kind
            || !labels.TryGetProperty(OperatorLabels.OwnerUid, out var u) || u.GetString() is not { Length: > 0 } uid)
            return null;
        return _runtimesByKind.TryGetValue(kind, out var runtime) && runtime.KeyOfUid(uid) is { } key ? (kind, key) : null;
    }

    /// <summary>A SQL pod belongs to the StatefulSet, not to the CR: its parent is read
    /// from its identity label. Otherwise <see cref="MapToParent(JsonElement)"/>.</summary>
    internal static (string ParentKind, string ParentKey)? MapToParent(string childKind, JsonElement obj)
    {
        if (childKind is PodOfInstance or PodOfAg)
        {
            if (!obj.TryGetProperty("metadata", out var meta) || !meta.TryGetProperty("labels", out var labels))
                return null;
            var ns = meta.TryGetProperty("namespace", out var n) ? n.GetString() : null;
            var (label, kind) = childKind == PodOfInstance ? (OperatorLabels.Instance, "SqlInstance") : (OperatorLabels.Ag, "SqlAvailabilityGroup");
            return labels.TryGetProperty(label, out var owner) && owner.GetString() is { Length: > 0 } name
                ? (kind, ns is null ? name : $"{ns}/{name}")
                : null;
        }
        return MapToParent(obj);
    }

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

    /// <summary>What a change must move to count: the generation of an object that has one; for a pod
    /// (none), a stable hash of its uid, phase, readiness, deletion and node — and, of what
    /// announces its stop: the DisruptionTarget condition and the disruption-requested-at annotation.</summary>
    internal static long? SignatureOf(JsonElement obj)
    {
        if (!obj.TryGetProperty("metadata", out var m))
            return null;
        if (m.TryGetProperty("generation", out var g) && g.ValueKind == JsonValueKind.Number)
            return g.GetInt64();
        if (!obj.TryGetProperty("status", out var status))
            return null;
        var uid = m.TryGetProperty("uid", out var u) ? u.GetString() : null;
        var deleting = m.TryGetProperty("deletionTimestamp", out var d) ? d.GetString() : null;
        var phase = status.TryGetProperty("phase", out var ph) ? ph.GetString() : null;
        var node = obj.TryGetProperty("spec", out var spec) && spec.TryGetProperty("nodeName", out var nn) ? nn.GetString() : null;
        var ready = status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array
            ? conditions.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "Ready")
                .Select(c => c.TryGetProperty("status", out var st) ? st.GetString() : null).FirstOrDefault()
            : null;
        var disruption = status.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array
            ? conds.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "DisruptionTarget")
                .Select(c => $"{(c.TryGetProperty("status", out var st) ? st.GetString() : null)}:{(c.TryGetProperty("reason", out var r) ? r.GetString() : null)}")
                .FirstOrDefault()
            : null;
        var requested = m.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object
                        && a.TryGetProperty(MsSqlOperator.Services.Maintenance.MaintenanceSignals.RequestedAtAnnotation, out var at)
            ? at.GetString()
            : null;
        var signature = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{uid}|{phase}|{ready}|{deleting}|{node}|{disruption}|{requested}"));
        return BitConverter.ToInt64(signature, 0);
    }

    private sealed record ChildKind(
        string Kind,
        Func<CancellationToken, Task<string>> ListJson,
        Func<string?, Action, Action<string, JsonElement>, CancellationToken, Task<string?>> WatchSession);
}
