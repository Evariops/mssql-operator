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

        var updated = await client.CustomObjects.ReplaceNamespacedCustomObjectStatusAsync(
            entity, gvk.Group, gvk.Version, ns, gvk.PluralName, name, cancellationToken: ct);

        // The custom-objects API returns the object as a loosely typed JSON document; round-trip it.
        return KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(updated));
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

    private static void SetGvk<T>(T entity) where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var gvk = GroupVersionKind.Of<T>();
        entity.ApiVersion = gvk.ApiVersion;
        entity.Kind = gvk.Kind;
    }
}
