using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public interface IAntiAffinityBuilder
{
    /// <summary>
    /// Builds pod anti-affinity. Owns ALL <c>podAntiAffinity</c> terms: self, cross-instance pairwise
    /// (<see cref="AntiAffinitySpec"/>), and optional pool-scoped hostname separation.
    /// </summary>
    /// <param name="spec">The anti-affinity spec, or null.</param>
    /// <param name="selfLabels">Pod labels matched by self anti-affinity terms.</param>
    /// <param name="pool">The resource's pool label value (from <c>spec.pool</c>).</param>
    /// <param name="poolAntiAffinity">"Required" | "Preferred" | null — strict cross-pool hostname
    /// separation. Ignored when <paramref name="pool"/> is empty.</param>
    V1Affinity? Build(
        AntiAffinitySpec? spec, Dictionary<string, string> selfLabels,
        string? pool = null, string? poolAntiAffinity = null);
}
