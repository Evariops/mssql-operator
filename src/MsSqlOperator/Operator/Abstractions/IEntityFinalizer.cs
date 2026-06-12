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
    /// Stable finalizer name: an explicit literal constant per CRD, in the
    /// conventional <c>domain/name</c> form (e.g. <c>mssql-operator.evariops.com/sqluser-finalizer</c>).
    /// Never derive it from a type name — renaming a class must not orphan deletions.
    /// </summary>
    string Identifier { get; }

    /// <summary>
    /// Previous identifiers this finalizer was known under; the runtime removes them whenever
    /// it patches finalizers, so objects created by older operator versions still delete.
    /// Drop entries once no supported version emits them.
    /// </summary>
    IReadOnlyList<string> LegacyIdentifiers => [];
}
