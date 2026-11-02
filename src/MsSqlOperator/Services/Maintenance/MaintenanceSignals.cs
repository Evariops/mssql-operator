using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MsSqlOperator.Core.Maintenance;

namespace MsSqlOperator.Services.Maintenance;

/// <summary>The signals seen for one pod of a resource.</summary>
public sealed record PodSignals(string Pod, string? Node, IReadOnlyList<MaintenanceSignal> Signals);

/// <summary>
/// What the SQL pod and node watches say about coming stops, kept in memory by the
/// leader. The owned-resource watcher feeds it; a reconcile reads it without any API call. Only the
/// facts that can announce a stop are kept, not the objects. Until the first list of the pods and of
/// the nodes has been read, <see cref="IsSynced"/> is false and nothing may be concluded from an
/// absence of signal: a restarted leader would otherwise clear the maintenance state of
/// every resource on its first pass.
/// </summary>
public sealed class MaintenanceSignals
{
    /// <summary>Set by the eviction webhook on the pod it delays: when the eviction was first asked.</summary>
    public const string RequestedAtAnnotation = "mssql-operator.evariops.com/disruption-requested-at";

    /// <summary>Who asked first (the user of the admission request); a node identity takes over.</summary>
    public const string RequestedByAnnotation = "mssql-operator.evariops.com/disruption-requested-by";

    /// <summary>When the eviction was last asked again, within 30 s: a request not repeated for 2 min
    /// is abandoned.</summary>
    public const string LastRequestAtAnnotation = "mssql-operator.evariops.com/disruption-last-request-at";

    /// <summary>The verdict the leader publishes for the eviction webhook: a JSON object
    /// with decision, reason, deadline and validUntil.</summary>
    public const string VerdictAnnotation = "mssql-operator.evariops.com/disruption-verdict";

    /// <summary>Every annotation of an eviction request, as the leader clears them.</summary>
    public static readonly IReadOnlyList<string> RequestAnnotations =
        [RequestedAtAnnotation, RequestedByAnnotation, LastRequestAtAnnotation, VerdictAnnotation];

    private sealed record PodEntry(string OwnerKind, string OwnerKey, PodFacts Facts);

    private readonly ConcurrentDictionary<string, PodEntry> _pods = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeFacts> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seeded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taints;
    private readonly int _podKinds;

    public MaintenanceSignals(IEnumerable<string>? extraTaints = null, int podKinds = 2)
    {
        _taints = new HashSet<string>(MaintenanceCore.DefaultDisruptionTaints.Concat(extraTaints ?? []), StringComparer.Ordinal);
        _podKinds = podKinds;
    }

    public IReadOnlyCollection<string> DisruptionTaints => _taints;

    /// <summary>True once the pods of every kind and the nodes have been listed.</summary>
    public bool IsSynced => _seeded.ContainsKey(NodesSeed) && _seeded.Count >= _podKinds + 1;

    private const string NodesSeed = "nodes";

    public void MarkPodsSeeded(string podKind) => _seeded.TryAdd(podKind, 0);

    public void MarkNodesSeeded() => _seeded.TryAdd(NodesSeed, 0);

    public bool IsNodesSeeded => _seeded.ContainsKey(NodesSeed);

    // ── Pods ──────────────────────────────────────────────

    public void ObservePod(string ownerKind, string ownerKey, JsonElement pod)
    {
        if (PodKey(pod) is not { } key)
            return;
        _pods[key] = new PodEntry(ownerKind, ownerKey, PodFactsOf(pod));
    }

    public void ForgetPod(JsonElement pod)
    {
        if (PodKey(pod) is { } key)
            _pods.TryRemove(key, out _);
    }

    /// <summary>A relist of the pods of <paramref name="ownerKind"/>: they replace what was known.</summary>
    public void ReplacePods(string ownerKind, IReadOnlyList<(string OwnerKey, JsonElement Pod)> pods)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ownerKey, pod) in pods)
        {
            if (PodKey(pod) is not { } key)
                continue;
            listed.Add(key);
            _pods[key] = new PodEntry(ownerKind, ownerKey, PodFactsOf(pod));
        }
        foreach (var gone in _pods.Where(kv => kv.Value.OwnerKind == ownerKind && !listed.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            _pods.TryRemove(gone, out _);
    }

    /// <summary>The owners of the SQL pods scheduled on <paramref name="node"/>.</summary>
    public IReadOnlyList<(string OwnerKind, string OwnerKey)> OwnersOn(string node)
        => _pods.Values.Where(p => p.Facts.Node == node).Select(p => (p.OwnerKind, p.OwnerKey)).Distinct().ToList();

    /// <summary>The facts of every known pod of a resource, signals or not.</summary>
    public IReadOnlyList<PodFacts> PodsOf(string ownerKind, string ownerKey)
        => _pods.Values
            .Where(p => p.OwnerKind == ownerKind && p.OwnerKey == ownerKey)
            .Select(p => p.Facts)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The pods of a resource that carry at least one signal, with their signals.</summary>
    public IReadOnlyList<PodSignals> For(string ownerKind, string ownerKey)
        => _pods.Values
            .Where(p => p.OwnerKind == ownerKind && p.OwnerKey == ownerKey)
            .Select(p => new PodSignals(p.Facts.Name, p.Facts.Node,
                MaintenanceCore.Classify(p.Facts, p.Facts.Node is { } n ? _nodes.GetValueOrDefault(n) : null, _taints)))
            .Where(p => p.Signals.Count > 0)
            .OrderBy(p => p.Pod, StringComparer.Ordinal)
            .ToList();

    // ── Nodes ─────────────────────────────────────────────

    /// <summary>Records the node; true when what it says about coming stops changed (cordon, a
    /// disruption taint, the shutdown), so that the owners of its pods are reconciled.</summary>
    public bool ObserveNode(JsonElement node)
    {
        var facts = NodeFactsOf(node);
        if (facts is null)
            return false;
        var previous = _nodes.GetValueOrDefault(facts.Name);
        _nodes[facts.Name] = facts;
        return previous is null ? MaintenanceCore.Classify(Probe(facts), facts, _taints).Count > 0 : Signature(previous) != Signature(facts);
    }

    public bool ForgetNode(JsonElement node)
        => NodeFactsOf(node) is { } facts && _nodes.TryRemove(facts.Name, out _);

    public string? NodeName(JsonElement node) => NodeFactsOf(node)?.Name;

    /// <summary>The node-scoped signals of a node, read through a pod that carries none of its own.</summary>
    private static PodFacts Probe(NodeFacts node) => new(string.Empty, node.Name, null, null, null, null);

    private string Signature(NodeFacts node)
        => string.Join('|', MaintenanceCore.Classify(Probe(node), node, _taints).Select(s => s.Kind.ToString()));

    // ── Parsing ───────────────────────────────────────────

    private static string? PodKey(JsonElement pod)
        => pod.TryGetProperty("metadata", out var m) && m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name
            ? $"{(m.TryGetProperty("namespace", out var ns) ? ns.GetString() : null)}/{name}"
            : null;

    internal static PodFacts PodFactsOf(JsonElement pod)
    {
        var meta = pod.TryGetProperty("metadata", out var m) ? m : default;
        var name = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var node = pod.TryGetProperty("spec", out var spec) && spec.TryGetProperty("nodeName", out var nn) ? nn.GetString() : null;
        var deleting = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("deletionTimestamp", out var d) ? Time(d) : null;
        DateTimeOffset? requested = null, last = null;
        string? by = null;
        if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty(RequestedAtAnnotation, out var at))
        {
            requested = Time(at);
            by = annotations.TryGetProperty(RequestedByAnnotation, out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            last = annotations.TryGetProperty(LastRequestAtAnnotation, out var l) ? Time(l) : null;
        }

        string? reason = null;
        DateTimeOffset? since = null;
        if (pod.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions)
            && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conditions.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var t) && t.GetString() == "DisruptionTarget"
                    && c.TryGetProperty("status", out var s) && s.GetString() == "True")
                {
                    reason = c.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown";
                    since = c.TryGetProperty("lastTransitionTime", out var lt) ? Time(lt) : null;
                }
            }
        }
        return new PodFacts(name, string.IsNullOrEmpty(node) ? null : node, deleting, reason, since, requested, by, last);
    }

    internal static NodeFacts? NodeFactsOf(JsonElement node)
    {
        if (!node.TryGetProperty("metadata", out var m) || !m.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name)
            return null;
        var spec = node.TryGetProperty("spec", out var sp) ? sp : default;
        var unschedulable = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("unschedulable", out var u) && u.ValueKind == JsonValueKind.True;
        var taints = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("taints", out var ts) && ts.ValueKind == JsonValueKind.Array
            ? ts.EnumerateArray().Select(t => t.TryGetProperty("key", out var k) ? k.GetString() : null).OfType<string>().ToList()
            : [];

        string? readyStatus = null, readyMessage = null;
        DateTimeOffset? readySince = null;
        if (node.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions)
            && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conditions.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var t) && t.GetString() == "Ready")
                {
                    readyStatus = c.TryGetProperty("status", out var s) ? s.GetString() : null;
                    readyMessage = c.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                    readySince = c.TryGetProperty("lastTransitionTime", out var lt) ? Time(lt) : null;
                }
            }
        }
        return new NodeFacts(name, unschedulable, taints, readyStatus, readyMessage, readySince);
    }

    private static DateTimeOffset? Time(JsonElement value)
        => value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}
