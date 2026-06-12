using System.Collections.Concurrent;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// <see cref="IKubernetesClient"/> implementation over the official <see cref="IKubernetes"/> client.
/// CRUD goes through <see cref="GenericClient"/> (resolved by GVK), the status subresource through
/// the custom-objects API.
/// </summary>
public sealed class KubernetesClientAdapter(IKubernetes client) : IKubernetesClient
{
    private readonly ConcurrentDictionary<Type, GenericClient> _generic = new();

    // Hash of the last status we wrote per object. Writing status triggers a Modified watch event,
    // which re-queues a reconcile, which recomputes the (identical) status and writes again — a tight
    // feedback loop that also amplified status growth. Skipping the PUT when the status is byte-for-byte
    // unchanged breaks the loop. We are the only writer of the status subresource, so the cache mirrors
    // the server; after an operator restart it is empty and the first write per object simply goes through.
    private readonly ConcurrentDictionary<string, string> _statusHashes = new();

    private GenericClient Generic<T>()
        => _generic.GetOrAdd(typeof(T), t =>
        {
            var gvk = GroupVersionKind.Of(t);
            return gvk.IsCoreGroup
                ? new GenericClient(client, gvk.Version, gvk.PluralName, disposeClient: false)
                : new GenericClient(client, gvk.Group, gvk.Version, gvk.PluralName, disposeClient: false);
        });

    public async Task<T?> GetAsync<T>(string name, string? @namespace = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var gc = Generic<T>();
        try
        {
            return string.IsNullOrEmpty(@namespace)
                ? await gc.ReadAsync<T>(name, ct)
                : await gc.ReadNamespacedAsync<T>(@namespace, name, ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IList<T>> ListAsync<T>(string? @namespace = null, string? labelSelector = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var gc = Generic<T>();
        var list = string.IsNullOrEmpty(@namespace)
            ? await gc.ListAsync<EntityList<T>>(labelSelector, null, null, null, ct)
            : await gc.ListNamespacedAsync<EntityList<T>>(@namespace, labelSelector, null, null, null, ct);
        return list.Items ?? [];
    }

    public async Task<T> CreateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gc = Generic<T>();
        var ns = entity.Metadata?.NamespaceProperty;
        return string.IsNullOrEmpty(ns)
            ? await gc.CreateAsync(entity, ct)
            : await gc.CreateNamespacedAsync(entity, ns, ct);
    }

    public async Task<T> UpdateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gc = Generic<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        return string.IsNullOrEmpty(ns)
            ? await gc.ReplaceAsync(entity, name, ct)
            : await gc.ReplaceNamespacedAsync(entity, ns, name, ct);
    }

    public async Task<T> UpdateStatusAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gvk = GroupVersionKind.Of<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // Skip the write when the status is unchanged (see _statusHashes) to avoid the
        // self-reconcile loop. Relies on controllers producing a stable status — conditions must
        // keep their lastTransitionTime when the state does not change (see ConditionOps).
        // The key includes the UID so a delete/recreate of the same name never
        // inherits the previous object's hash.
        var key = $"{gvk.Group}/{gvk.PluralName}/{ns}/{name}/{entity.Metadata.Uid}";
        var hash = StatusHash(entity);
        if (hash is not null && _statusHashes.TryGetValue(key, out var prev) && prev == hash)
            return entity;

        var updated = await client.CustomObjects.ReplaceNamespacedCustomObjectStatusAsync(
            entity, gvk.Group, gvk.Version, ns, gvk.PluralName, name, cancellationToken: ct);

        if (hash is not null)
            _statusHashes[key] = hash;

        // The custom-objects API returns the object as a loosely typed JSON document; round-trip it.
        return KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(updated));
    }

    /// <summary>Drops cached status hashes for a deleted object, so a future
    /// object reusing the name starts clean. Called by the runtime on deletion.</summary>
    public void ForgetStatusHashes(string? ns, string name)
    {
        var suffix = $"/{ns}/{name}/";
        foreach (var key in _statusHashes.Keys.Where(k => k.Contains(suffix, StringComparison.Ordinal)))
            _statusHashes.TryRemove(key, out _);
    }

    // Cache the Status property lookup per closed type (it ran on every status write).
    private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _statusProps = new();

    private static string? StatusHash<T>(T entity)
    {
        var statusProp = _statusProps.GetOrAdd(typeof(T), t => t.GetProperty("Status"));
        var status = statusProp?.GetValue(entity);
        if (status is null) return null;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(KubernetesJson.Serialize(status)));
        return Convert.ToHexStringLower(bytes);
    }

    public async Task DeleteAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var gc = Generic<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        try
        {
            if (string.IsNullOrEmpty(ns))
                await gc.DeleteAsync<T>(name, ct);
            else
                await gc.DeleteNamespacedAsync<T>(ns, name, ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // already gone — idempotent delete
        }
    }

    public async Task ForceDeletePodAsync(string name, string @namespace, CancellationToken ct = default)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(
                name, @namespace,
                gracePeriodSeconds: 0,
                propagationPolicy: "Background",
                cancellationToken: ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // already gone — idempotent delete
        }
    }

    private static void SetGvk<T>(T entity) where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var gvk = GroupVersionKind.Of<T>();
        entity.ApiVersion = gvk.ApiVersion;
        entity.Kind = gvk.Kind;
    }
}
