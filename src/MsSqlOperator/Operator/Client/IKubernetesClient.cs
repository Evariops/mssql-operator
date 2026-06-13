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
}
