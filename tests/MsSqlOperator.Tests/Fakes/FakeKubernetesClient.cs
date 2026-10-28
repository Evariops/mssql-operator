using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>
/// Hand-written fake for <see cref="IKubernetesClient"/> (replaces Moq). Configure return values
/// with the <c>Set*</c> helpers; assert interactions on the public <c>*Calls</c> lists.
/// </summary>
public sealed class FakeKubernetesClient : IKubernetesClient
{
    // Configured GetAsync responses, keyed by (type, name, namespace). A present key with a null
    // value means "configured to return null"; an absent key also returns null.
    private readonly Dictionary<(string Type, string Name, string? Ns), object?> _gets = new();
    private readonly Dictionary<(string Type, string? Selector), object> _lists = new();     // -> IList<T>
    private readonly Dictionary<(string Type, string? Selector), object> _listFuncs = new(); // -> Func<IList<T>>
    private readonly Dictionary<string, object> _createReturns = new(); // type -> override return
    private readonly Dictionary<string, object> _applyReturns = new();

    /// <summary>When true, Create/Update write the entity into an in-memory store keyed by
    /// (type, name, namespace) and Get reads it back — emulating a real API server round-trip.</summary>
    public bool StoreWrites { get; set; }
    private readonly Dictionary<(string Type, string? Name, string? Ns), object> _store = new();

    public readonly List<(string Type, string Name, string? Ns)> GetCalls = [];
    public readonly List<(string Type, string? Ns, string? LabelSelector)> ListCalls = [];
    public readonly List<object> CreateCalls = [];
    public readonly List<object> UpdateCalls = [];
    public readonly List<object> UpdateStatusCalls = [];
    public readonly List<object> DeleteCalls = [];
    public readonly List<object> ApplyCalls = [];
    public readonly List<(string Name, string Ns)> ForceDeletePodCalls = [];
    public readonly List<(string Name, string Ns)> DeleteStatefulSetOrphanCalls = [];
    public readonly List<(string Name, string Ns, IReadOnlyDictionary<string, string> Labels)> PatchPodLabelsCalls = [];
    public readonly List<(string? Ns, string Name)> ForgetStatusHashesCalls = [];

    // ── Configuration ───────────────────────────────────────────────────
    public void SetGet<T>(string name, string? ns, T? value) => _gets[(typeof(T).Name, name, ns)] = value;

    private readonly Dictionary<(string Type, string Name, string? Ns), Exception> _getFailures = [];

    /// <summary>GetAsync of this object throws <paramref name="failure"/> (API error, timeout).</summary>
    public void SetGetFailure<T>(string name, string? ns, Exception failure) => _getFailures[(typeof(T).Name, name, ns)] = failure;

    /// <summary>Pre-load an object into the in-memory store (keyed by its metadata) so a later Get
    /// returns it. Use with <see cref="StoreWrites"/> to emulate an existing object.</summary>
    public void Seed<T>(T entity) where T : IKubernetesObject<V1ObjectMeta>
        => _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity!;
    public void SetList<T>(IList<T> items, string? labelSelector = null) => _lists[(typeof(T).Name, labelSelector)] = items;
    /// <summary>Configure a list result computed per call (e.g. to return different items across calls).</summary>
    public void SetListFunc<T>(Func<IList<T>> func, string? labelSelector = null) => _listFuncs[(typeof(T).Name, labelSelector)] = func;
    public void SetCreateReturn<T>(T value) => _createReturns[typeof(T).Name] = value!;
    public void SetApplyReturn<T>(T value) => _applyReturns[typeof(T).Name] = value!;

    // ── Interface ───────────────────────────────────────────────────────
    public Task<T?> GetAsync<T>(string name, string? @namespace = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        GetCalls.Add((typeof(T).Name, name, @namespace));
        if (_getFailures.TryGetValue((typeof(T).Name, name, @namespace), out var failure))
            return Task.FromException<T?>(failure);
        if (StoreWrites && _store.TryGetValue((typeof(T).Name, name, @namespace), out var stored))
            return Task.FromResult((T?)stored);
        return Task.FromResult(_gets.TryGetValue((typeof(T).Name, name, @namespace), out var v) ? (T?)v : null);
    }

    public Task<IList<T>> ListAsync<T>(string? @namespace = null, string? labelSelector = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        ListCalls.Add((typeof(T).Name, @namespace, labelSelector));
        if (_listFuncs.TryGetValue((typeof(T).Name, labelSelector), out var f))
            return Task.FromResult(((Func<IList<T>>)f)());
        return Task.FromResult(_lists.TryGetValue((typeof(T).Name, labelSelector), out var v) ? (IList<T>)v : new List<T>());
    }

    public Task<T> CreateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        CreateCalls.Add(entity);
        if (StoreWrites) _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity;
        return Task.FromResult(_createReturns.TryGetValue(typeof(T).Name, out var v) ? (T)v : entity);
    }

    public Task<T> UpdateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        if (MsSqlOperator.Operator.Client.JsonPatch.NoPut(typeof(T)) is { } why)
            return Task.FromException<T>(new InvalidOperationException($"PUT of a {typeof(T).Name} refused: {why}"));
        UpdateCalls.Add(entity);
        if (StoreWrites) _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity;
        return Task.FromResult(entity);
    }

    public readonly List<(object Entity, IReadOnlyList<Dictionary<string, object?>> Operations)> PatchCalls = [];

    public Task<T> PatchAsync<T>(T entity, IReadOnlyList<Dictionary<string, object?>> operations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        PatchCalls.Add((entity, operations));
        return Task.FromResult(entity);
    }

    public readonly List<(object Entity, IReadOnlyDictionary<string, string?> Annotations)> PatchMetadataCalls = [];

    /// <summary>When set, PatchMetadataAsync throws it (a 409/422 on the resourceVersion test).</summary>
    public Exception? PatchMetadataFailure { get; set; }

    public Task<T> PatchMetadataAsync<T>(T entity, IReadOnlyDictionary<string, string?> annotations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        if (PatchMetadataFailure is not null)
            return Task.FromException<T>(PatchMetadataFailure);
        PatchMetadataCalls.Add((entity, annotations));
        var current = entity.Metadata.Annotations ??= new Dictionary<string, string>();
        foreach (var (key, value) in annotations)
            if (value is null) current.Remove(key);
            else current[key] = value;
        if (StoreWrites) _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity;
        return Task.FromResult(entity);
    }

    public readonly List<(object Entity, string FieldManager, IReadOnlyDictionary<string, object?> Status)> ApplyStatusCalls = [];

    public Task<T> ApplyStatusAsync<T>(T entity, string fieldManager, IReadOnlyDictionary<string, object?> status, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        ApplyStatusCalls.Add((entity, fieldManager, status));
        if (NextStatusResourceVersion is { } resourceVersion)
            entity.Metadata.ResourceVersion = resourceVersion;
        return Task.FromResult(entity);
    }

    /// <summary>When set, UpdateStatusAsync returns a copy carrying this resourceVersion, as the API
    /// server does — the caller must report it to avoid a 409 on its next write.</summary>
    public string? NextStatusResourceVersion { get; set; }

    public Task<T> UpdateStatusAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        UpdateStatusCalls.Add(entity);
        if (NextStatusResourceVersion is { } resourceVersion)
        {
            var copy = KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(entity));
            copy.Metadata.ResourceVersion = resourceVersion;
            return Task.FromResult(copy);
        }
        return Task.FromResult(entity);
    }

    /// <summary>Every status patch, with its keys, in order.</summary>
    public readonly List<(object Entity, IReadOnlyDictionary<string, object?> Patch)> PatchStatusCalls = [];

    /// <summary>Makes every status patch fail (a 409, a 5xx, a timeout): the intention is not persisted.</summary>
    public Exception? PatchStatusFailure { get; set; }

    public Task<T> PatchStatusAsync<T>(T entity, IReadOnlyDictionary<string, object?> statusPatch, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        if (PatchStatusFailure is not null)
            return Task.FromException<T>(PatchStatusFailure);
        PatchStatusCalls.Add((entity, statusPatch));
        return Task.FromResult(entity);
    }

    public Task DeleteAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        DeleteCalls.Add(entity);
        return Task.CompletedTask;
    }

    public readonly List<(string Name, string Ns, int GracePeriodSeconds)> DeletePodCalls = [];

    public Task DeletePodAsync(string name, string @namespace, int gracePeriodSeconds, CancellationToken ct = default)
    {
        DeletePodCalls.Add((name, @namespace, gracePeriodSeconds));
        return Task.CompletedTask;
    }

    public Task ForceDeletePodAsync(string name, string @namespace, CancellationToken ct = default)
    {
        ForceDeletePodCalls.Add((name, @namespace));
        return Task.CompletedTask;
    }

    public Task DeleteStatefulSetOrphanAsync(string name, string @namespace, CancellationToken ct = default)
    {
        DeleteStatefulSetOrphanCalls.Add((name, @namespace));
        // The API server releases the name once the orphan delete completes.
        _store.Remove((nameof(V1StatefulSet), name, @namespace));
        _gets.Remove((nameof(V1StatefulSet), name, @namespace));
        return Task.CompletedTask;
    }

    public void ForgetStatusHashes(string? @namespace, string name) => ForgetStatusHashesCalls.Add((@namespace, name));

    public Task PatchPodLabelsAsync(string name, string @namespace, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        PatchPodLabelsCalls.Add((name, @namespace, labels));
        return Task.CompletedTask;
    }

    public Task<T> ApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        ApplyCalls.Add(entity);
        return Task.FromResult(_applyReturns.TryGetValue(typeof(T).Name, out var v) ? (T)v : entity);
    }

    public readonly List<object> DryRunApplyCalls = [];

    /// <summary>What a dry-run apply returns; null ⇒ the entity itself.</summary>
    public Func<object, object>? DryRunApplyResult { get; set; }

    public Task<T> DryRunApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        DryRunApplyCalls.Add(entity);
        return Task.FromResult(DryRunApplyResult is { } result ? (T)result(entity) : entity);
    }

    // ── Assertion helpers (typed views over the recorded calls) ─────────
    public IEnumerable<T> Created<T>() => CreateCalls.OfType<T>();
    public IEnumerable<T> Updated<T>() => UpdateCalls.OfType<T>();
    public IEnumerable<T> StatusUpdated<T>() => UpdateStatusCalls.OfType<T>();
    public IEnumerable<T> Deleted<T>() => DeleteCalls.OfType<T>();
    public IEnumerable<T> Applied<T>() => ApplyCalls.OfType<T>();
}
