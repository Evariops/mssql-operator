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
    private readonly Dictionary<string, object> _lists = new();         // type -> IList<T> (boxed)
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

    /// <summary>Pre-load an object into the in-memory store (keyed by its metadata) so a later Get
    /// returns it. Use with <see cref="StoreWrites"/> to emulate an existing object.</summary>
    public void Seed<T>(T entity) where T : IKubernetesObject<V1ObjectMeta>
        => _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity!;
    public void SetList<T>(IList<T> items) => _lists[typeof(T).Name] = items;
    public void SetCreateReturn<T>(T value) => _createReturns[typeof(T).Name] = value!;
    public void SetApplyReturn<T>(T value) => _applyReturns[typeof(T).Name] = value!;

    // ── Interface ───────────────────────────────────────────────────────
    public Task<T?> GetAsync<T>(string name, string? @namespace = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        GetCalls.Add((typeof(T).Name, name, @namespace));
        if (StoreWrites && _store.TryGetValue((typeof(T).Name, name, @namespace), out var stored))
            return Task.FromResult((T?)stored);
        return Task.FromResult(_gets.TryGetValue((typeof(T).Name, name, @namespace), out var v) ? (T?)v : null);
    }

    public Task<IList<T>> ListAsync<T>(string? @namespace = null, string? labelSelector = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        ListCalls.Add((typeof(T).Name, @namespace, labelSelector));
        return Task.FromResult(_lists.TryGetValue(typeof(T).Name, out var v) ? (IList<T>)v : new List<T>());
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
        UpdateCalls.Add(entity);
        if (StoreWrites) _store[(typeof(T).Name, entity.Metadata?.Name, entity.Metadata?.NamespaceProperty)] = entity;
        return Task.FromResult(entity);
    }

    public Task<T> UpdateStatusAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        UpdateStatusCalls.Add(entity);
        return Task.FromResult(entity);
    }

    public Task DeleteAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        DeleteCalls.Add(entity);
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

    // ── Assertion helpers (typed views over the recorded calls) ─────────
    public IEnumerable<T> Created<T>() => CreateCalls.OfType<T>();
    public IEnumerable<T> Updated<T>() => UpdateCalls.OfType<T>();
    public IEnumerable<T> StatusUpdated<T>() => UpdateStatusCalls.OfType<T>();
    public IEnumerable<T> Deleted<T>() => DeleteCalls.OfType<T>();
    public IEnumerable<T> Applied<T>() => ApplyCalls.OfType<T>();
}
