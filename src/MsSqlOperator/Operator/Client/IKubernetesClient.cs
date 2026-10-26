using k8s;
using k8s.Models;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Typed, generic Kubernetes client, replacing KubeOps' <c>IKubernetesClient</c>.
/// Works for native resources (<c>V1Secret</c>, <c>V1Pod</c>, …) and our custom resources alike.
/// </summary>
public interface IKubernetesClient
{
    /// <summary>Returns the object, or <c>null</c> if it does not exist (404).</summary>
    Task<T?> GetAsync<T>(string name, string? @namespace = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>Lists objects, optionally scoped to a namespace and filtered by label selector.</summary>
    Task<IList<T>> ListAsync<T>(string? @namespace = null, string? labelSelector = null, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    Task<T> CreateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    Task<T> UpdateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>Updates the <c>/status</c> subresource of a custom resource.</summary>
    Task<T> UpdateStatusAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    Task DeleteAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// Immediately deletes a pod (gracePeriodSeconds=0, background propagation). The
    /// generic delete carries no options, so the previous "force" deletes were ordinary deletes
    /// — a stuck Terminating pod on a dead node never actually left etcd.
    /// </summary>
    Task ForceDeletePodAsync(string name, string @namespace, CancellationToken ct = default);

    /// <summary>
    /// Deletes a StatefulSet WITHOUT deleting its pods (propagationPolicy=Orphan).
    /// volumeClaimTemplates are immutable, so a storage growth patches the PVCs then recreates
    /// the StatefulSet around the live pods.
    /// </summary>
    Task DeleteStatefulSetOrphanAsync(string name, string @namespace, CancellationToken ct = default);

    /// <summary>Drops cached status hashes for a deleted object.</summary>
    void ForgetStatusHashes(string? @namespace, string name);

    /// <summary>
    /// Merge-patches only the given labels onto a pod — no full-object PUT, so the
    /// operator never 409s against the kubelet or clobbers fields written by other actors.
    /// </summary>
    Task PatchPodLabelsAsync(string name, string @namespace, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default);

    /// <summary>
    /// Server-side apply: PATCH application/apply-patch+yaml with
    /// fieldManager=mssql-operator and force on owned fields. Replaces the GET+PUT pattern,
    /// which 422'd in a loop on immutable StatefulSet fields and clobbered fields written by
    /// other actors (mutating webhooks, sidecar injectors).
    /// </summary>
    Task<T> ApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// The same server-side apply with <c>dryRun=All</c>: returns the object the API server would
    /// store (merged with the fields of other managers, defaulted, mutated by webhooks) without
    /// persisting anything.
    /// </summary>
    Task<T> DryRunApplyAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;
}
