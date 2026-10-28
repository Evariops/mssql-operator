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
        if (JsonPatch.NoPut(typeof(T)) is { } why)
            throw new InvalidOperationException($"PUT of a {typeof(T).Name} refused: {why}");
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

    public async Task<T> PatchStatusAsync<T>(T entity, IReadOnlyDictionary<string, object?> statusPatch, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gvk = GroupVersionKind.Of<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        var metadata = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(entity.Metadata.ResourceVersion))
            metadata["resourceVersion"] = entity.Metadata.ResourceVersion;
        var body = new Dictionary<string, object?> { ["metadata"] = metadata, ["status"] = statusPatch };
        var updated = await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
            new V1Patch(KubernetesJson.Serialize(body), V1Patch.PatchType.MergePatch),
            gvk.Group, gvk.Version, ns, gvk.PluralName, name, cancellationToken: ct);
        var result = KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(updated));

        entity.Metadata.ResourceVersion = result.Metadata.ResourceVersion;
        // The stored status changed behind the hash cache: the next full write must not be skipped.
        _statusHashes.TryRemove($"{gvk.Group}/{gvk.PluralName}/{ns}/{name}/{entity.Metadata.Uid}", out _);
        return result;
    }

    public async Task<T> PatchMetadataAsync<T>(T entity, IReadOnlyDictionary<string, string?> annotations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        if (annotations.Count == 0)
            return entity;
        var result = await SendPatchAsync(entity, MetadataPatch(entity, annotations), ct);
        entity.Metadata.Annotations = result.Metadata.Annotations;
        return result;
    }

    public Task<T> PatchAsync<T>(T entity, IReadOnlyList<Dictionary<string, object?>> operations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var ops = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(entity.Metadata.ResourceVersion))
            ops.Add(JsonPatch.Test("/metadata/resourceVersion", entity.Metadata.ResourceVersion));
        ops.AddRange(operations);
        return SendPatchAsync(entity, ops, ct);
    }

    private async Task<T> SendPatchAsync<T>(T entity, List<Dictionary<string, object?>> operations, CancellationToken ct)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gc = Generic<T>();
        var patch = new V1Patch(KubernetesJson.Serialize(operations), V1Patch.PatchType.JsonPatch);
        var ns = entity.Metadata.NamespaceProperty;
        var result = string.IsNullOrEmpty(ns)
            ? await gc.PatchAsync<T>(patch, entity.Metadata.Name, ct)
            : await gc.PatchNamespacedAsync<T>(patch, ns, entity.Metadata.Name, ct);
        entity.Metadata.ResourceVersion = result.Metadata.ResourceVersion;
        return result;
    }

    public async Task<T> ApplyStatusAsync<T>(T entity, string fieldManager, IReadOnlyDictionary<string, object?> status, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gvk = GroupVersionKind.Of<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var body = new Dictionary<string, object?>
        {
            ["apiVersion"] = entity.ApiVersion,
            ["kind"] = entity.Kind,
            ["metadata"] = new Dictionary<string, object?> { ["name"] = name, ["namespace"] = ns },
            ["status"] = status,
        };
        var updated = await client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
            new V1Patch(KubernetesJson.Serialize(body), V1Patch.PatchType.ApplyPatch),
            gvk.Group, gvk.Version, ns, gvk.PluralName, name, fieldManager: fieldManager, force: true, cancellationToken: ct);
        var result = KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(updated));
        entity.Metadata.ResourceVersion = result.Metadata.ResourceVersion;
        // The stored status changed behind the hash cache of the other writers.
        _statusHashes.TryRemove($"{gvk.Group}/{gvk.PluralName}/{ns}/{name}/{entity.Metadata.Uid}", out _);
        return result;
    }

    /// <summary>The JSON Patch operations: the resourceVersion test, then one operation per key
    /// (RFC 6901 escaping: ~ as ~0, / as ~1 — annotation keys carry a /).</summary>
    internal static List<Dictionary<string, object?>> MetadataPatch<T>(T entity, IReadOnlyDictionary<string, string?> annotations)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var ops = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(entity.Metadata.ResourceVersion))
            ops.Add(JsonPatch.Test("/metadata/resourceVersion", entity.Metadata.ResourceVersion));
        var current = entity.Metadata.Annotations;
        if (current is null && annotations.Any(a => a.Value is not null))
        {
            ops.Add(new() { ["op"] = "add", ["path"] = "/metadata/annotations",
                ["value"] = annotations.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => a.Value) });
            return ops;
        }
        foreach (var (key, value) in annotations)
        {
            var path = "/metadata/annotations/" + JsonPatch.Escape(key);
            if (value is null)
            {
                if (current?.ContainsKey(key) == true)
                    ops.Add(new() { ["op"] = "remove", ["path"] = path });
            }
            else
                ops.Add(new() { ["op"] = "add", ["path"] = path, ["value"] = value });
        }
        return ops;
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

    public Task<T> ApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
        => SendApplyAsync(entity, dryRun: false, ct);

    public Task<T> DryRunApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>
        => SendApplyAsync(entity, dryRun: true, ct);

    private async Task<T> SendApplyAsync<T>(T entity, bool dryRun, CancellationToken ct)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        SetGvk(entity);
        var gvk = GroupVersionKind.Of<T>();
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // SSA rejects resourceVersion/managedFields in the applied configuration.
        entity.Metadata.ResourceVersion = null;
        entity.Metadata.ManagedFields = null;

        var basePath = gvk.IsCoreGroup ? $"api/{gvk.Version}" : $"apis/{gvk.Group}/{gvk.Version}";
        var path = string.IsNullOrEmpty(ns)
            ? $"{basePath}/{gvk.PluralName}/{Uri.EscapeDataString(name)}"
            : $"{basePath}/namespaces/{Uri.EscapeDataString(ns)}/{gvk.PluralName}/{Uri.EscapeDataString(name)}";

        var typed = (Kubernetes)client;
        var uri = new Uri(typed.BaseUri, $"{path}?fieldManager=mssql-operator&force=true{(dryRun ? "&dryRun=All" : "")}");

        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(KubernetesJson.Serialize(entity),
                System.Text.Encoding.UTF8, "application/apply-patch+yaml"),
        };
        if (typed.Credentials != null)
            await typed.Credentials.ProcessHttpRequestAsync(request, ct);

        using var response = await typed.HttpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpOperationException($"Server-side apply of {gvk.Kind} {ns}/{name} failed: {(int)response.StatusCode} {body}")
            {
                Response = new HttpResponseMessageWrapper(response, body),
            };

        return KubernetesJson.Deserialize<T>(body);
    }

    public async Task PatchPodLabelsAsync(string name, string @namespace, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var patch = new V1Patch(
            KubernetesJson.Serialize(new { metadata = new { labels } }),
            V1Patch.PatchType.MergePatch);
        await client.CoreV1.PatchNamespacedPodAsync(patch, name, @namespace, cancellationToken: ct);
    }

    public async Task DeleteStatefulSetOrphanAsync(string name, string @namespace, CancellationToken ct = default)
    {
        try
        {
            await client.AppsV1.DeleteNamespacedStatefulSetAsync(
                name, @namespace, propagationPolicy: "Orphan", cancellationToken: ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // already gone — idempotent delete
        }
    }

    public async Task DeletePodAsync(string name, string @namespace, int gracePeriodSeconds, CancellationToken ct = default)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(
                name, @namespace,
                gracePeriodSeconds: gracePeriodSeconds,
                propagationPolicy: "Background",
                cancellationToken: ct);
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
