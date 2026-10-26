using k8s;
using k8s.Models;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Fake sa convergence: records the pods it was asked to converge and
/// reports the configured outcome.</summary>
public sealed class FakeSaPasswordConverger : ISaPasswordConverger
{
    public SaConvergence Outcome { get; set; } = SaConvergence.Converged;
    public readonly List<IReadOnlyList<string>> Calls = [];

    public Task<SaConvergence> ConvergeAsync(IKubernetesObject<V1ObjectMeta> owner, IReadOnlyList<string> pods, string saPassword, CancellationToken ct)
    {
        Calls.Add(pods);
        return Task.FromResult(Outcome);
    }
}

/// <summary>Fake fleet rollout budget: always allows unless told otherwise.</summary>
public sealed class FakeRolloutBudget : IRolloutBudget
{
    public bool Allowed { get; set; } = true;
    public bool TemplateChanges { get; set; } = true;
    public int MayStartCalls { get; private set; }

    public Task<(bool Allowed, string? HeldBy)> MayStartAsync(string ns, string name, CancellationToken ct)
    {
        MayStartCalls++;
        return Task.FromResult(Allowed ? (true, (string?)null) : (false, "other/instance"));
    }

    public Task<bool> ChangesTemplateAsync(V1StatefulSet desired, V1StatefulSet live, CancellationToken ct)
        => Task.FromResult(TemplateChanges);
}
