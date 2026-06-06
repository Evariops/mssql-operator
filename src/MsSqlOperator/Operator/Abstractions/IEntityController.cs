using k8s;
using k8s.Models;

namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Reconciler for a custom resource, replacing KubeOps' <c>IEntityController</c>.
/// <see cref="ReconcileAsync"/> is invoked on add/modify (and periodic requeue);
/// <see cref="DeletedAsync"/> is invoked once the object has been observed deleted.
/// </summary>
public interface IEntityController<TEntity>
    where TEntity : IKubernetesObject<V1ObjectMeta>
{
    Task<ReconciliationResult<TEntity>> ReconcileAsync(TEntity entity, CancellationToken ct);

    Task<ReconciliationResult<TEntity>> DeletedAsync(TEntity entity, CancellationToken ct);
}
