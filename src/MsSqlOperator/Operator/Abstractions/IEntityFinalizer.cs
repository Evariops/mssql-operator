using k8s;
using k8s.Models;

namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Finalizer for a custom resource, replacing KubeOps' <c>IEntityFinalizer</c>.
/// The runtime adds <see cref="Identifier"/> to the object's finalizers while it is alive,
/// runs <see cref="FinalizeAsync"/> when a deletion is requested, then removes the finalizer.
/// </summary>
public interface IEntityFinalizer<TEntity>
    where TEntity : IKubernetesObject<V1ObjectMeta>
{
    Task<ReconciliationResult<TEntity>> FinalizeAsync(TEntity entity, CancellationToken ct);

    /// <summary>
    /// Stable finalizer name. Derived from the implementing type, e.g.
    /// <c>sqlinstancefinalizer.finalizers.mssql-operator.evariops.com</c>. Must be a qualified name.
    /// </summary>
    string Identifier => $"{GetType().Name.ToLowerInvariant()}.finalizers.mssql-operator.evariops.com";
}
