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

    public IReadOnlyList<string> Stuck { get; set; } = [];

    public Task<RolloutDecision> MayStartAsync(string ns, string name, CancellationToken ct)
    {
        MayStartCalls++;
        return Task.FromResult(new RolloutDecision(Allowed, Allowed ? null : "other/instance", Stuck));
    }

    public Task<bool> ChangesTemplateAsync(V1StatefulSet desired, V1StatefulSet live, CancellationToken ct)
        => Task.FromResult(TemplateChanges);
}
