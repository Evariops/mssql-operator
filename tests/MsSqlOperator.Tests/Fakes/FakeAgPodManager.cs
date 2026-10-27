using k8s.Models;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IAgPodManager"/>: records label/fence operations and
/// serves a configurable pod list and readiness/fence outcomes.</summary>
public sealed class FakeAgPodManager : IAgPodManager
{
    public List<V1Pod> AgPods { get; set; } = [];
    public Func<string, bool> PodReady { get; set; } = _ => true;

    public readonly List<(string Pod, string Role)> RoleLabelCalls = [];
    public readonly List<(string Pod, string SyncState, bool FailoverReady)> SyncLabelCalls = [];
    public readonly List<(string Pod, bool Readable, bool ReadIntentReady, string ListenerReady)> ReadabilityCalls = [];
    /// <summary>Whether the read-only listener selects each pod, last value per pod.</summary>
    public readonly Dictionary<string, bool> ReadOnlyTargets = [];

    public Task SetPodRoleLabelAsync(string podName, string ns, string agName, string role, CancellationToken ct = default)
    {
        RoleLabelCalls.Add((podName, role));
        return Task.CompletedTask;
    }

    public Task SetPodSyncLabelsAsync(string podName, string ns, string syncState, bool failoverReady, CancellationToken ct = default)
    {
        SyncLabelCalls.Add((podName, syncState, failoverReady));
        return Task.CompletedTask;
    }

    public Task SetReadabilityLabelsAsync(string podName, string ns, bool readable, bool readIntentReady, string listenerReady, CancellationToken ct = default,
        bool? readOnlyTarget = null)
    {
        ReadabilityCalls.Add((podName, readable, readIntentReady, listenerReady));
        ReadOnlyTargets[podName] = readOnlyTarget ?? readIntentReady;
        return Task.CompletedTask;
    }

    public Task<bool> IsPodReadyAsync(string podName, string ns, CancellationToken ct = default) => Task.FromResult(PodReady(podName));

    public Task<List<V1Pod>> GetAgPodsAsync(string stsName, string ns, string agName, CancellationToken ct = default) => Task.FromResult(AgPods);
}
