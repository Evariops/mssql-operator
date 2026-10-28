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

    /// <summary>
    /// Merge-patches only <paramref name="statusPatch"/> (camelCase keys of the status) onto the
    /// <c>/status</c> subresource, with the entity's <c>resourceVersion</c> as precondition: a write
    /// raced by another one fails (409) instead of being lost. The new resourceVersion is reported
    /// on <paramref name="entity"/>, so that the status written at the end of the pass does not 409.
    /// Used to persist an intention before its effect; a failure means: do not act.
    /// </summary>
    Task<T> PatchStatusAsync<T>(T entity, IReadOnlyDictionary<string, object?> statusPatch, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// JSON Patch of any object, preceded by a <c>test</c> of its <c>resourceVersion</c>
    /// (<see cref="JsonPatch"/> builds the operations). A PUT wrote back every field as read; a
    /// patch changes what it names, and a concurrent change makes it fail instead of being lost.
    /// The new resourceVersion is reported on <paramref name="entity"/>.
    /// </summary>
    Task<T> PatchAsync<T>(T entity, IReadOnlyList<Dictionary<string, object?>> operations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// JSON Patch of the annotations of a custom resource (a null value removes the key), with
    /// a <c>test</c> of its <c>resourceVersion</c>. Consuming an annotation with a PUT of the whole CR
    /// wrote back the spec as the operator had read it — racing GitOps and every other writer; the
    /// patch touches the annotations only, and a concurrent change makes it fail instead of being
    /// lost. The new resourceVersion and annotations are reported on <paramref name="entity"/>.
    /// </summary>
    Task<T> PatchMetadataAsync<T>(T entity, IReadOnlyDictionary<string, string?> annotations, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// Server-side apply of a status fragment on <c>/status</c>, under
    /// <paramref name="fieldManager"/>. Conditions are a list-map keyed by type: applying one
    /// condition leaves the others alone, and the component that owns a condition is the one that
    /// changes it — no 409 on a status another component wrote meanwhile. The new resourceVersion is
    /// reported on <paramref name="entity"/>.
    /// </summary>
    Task<T> ApplyStatusAsync<T>(T entity, string fieldManager, IReadOnlyDictionary<string, object?> status, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    Task DeleteAsync<T>(T entity, CancellationToken ct = default)
        where T : class, IKubernetesObject<V1ObjectMeta>;

    /// <summary>
    /// Immediately deletes a pod (gracePeriodSeconds=0, background propagation). The
    /// generic delete carries no options, so the previous "force" deletes were ordinary deletes
    /// — a stuck Terminating pod on a dead node never actually left etcd.
    /// </summary>
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.ForceDeletePod)]
    Task ForceDeletePodAsync(string name, string @namespace, CancellationToken ct = default);

    /// <summary>Deletes a pod gracefully, with a grace period shorter than its own: the
    /// kubelet still runs the preStop hook and confirms the kill.</summary>
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DeletePod)]
    Task DeletePodAsync(string name, string @namespace, int gracePeriodSeconds, CancellationToken ct = default);

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
