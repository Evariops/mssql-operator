using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public interface ITopologySpreadBuilder
{
    /// <summary>
    /// Builds the native <c>topologySpreadConstraints</c> for a pod template.
    /// </summary>
    /// <param name="spec">The spread spec, or null for none.</param>
    /// <param name="pool">The resource's pool label value (from <c>spec.pool</c>), used by pool-scoped rules.</param>
    /// <param name="identityLabels">The resource's stable identity selector (e.g. <c>{ ag: name }</c>),
    /// used by <see cref="TopologySpreadSpec.Self"/> rules.</param>
    List<V1TopologySpreadConstraint>? Build(
        TopologySpreadSpec? spec, string? pool, IReadOnlyDictionary<string, string> identityLabels);
}
