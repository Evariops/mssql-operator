using MsSqlOperator.Core.Maintenance;

namespace MsSqlOperator.Tests.Core;

/// <summary>The signals of a coming stop, and their scope.</summary>
public class MaintenanceCoreTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static PodFacts Pod(DateTimeOffset? deleting = null, string? disruption = null, DateTimeOffset? evictionRequested = null, string? node = "n1")
        => new("ag-0", node, deleting, disruption, disruption is null ? null : T, evictionRequested);

    private static NodeFacts Node(bool cordoned = false, string[]? taints = null, string? ready = "True", string? message = null)
        => new("n1", cordoned, taints ?? [], ready, message, T);

    private static IReadOnlyList<(SignalKind, SignalScope)> Classify(PodFacts pod, NodeFacts? node)
        => MaintenanceCore.Classify(pod, node, MaintenanceCore.DefaultDisruptionTaints.ToHashSet()).Select(s => (s.Kind, s.Scope)).ToList();

    [Fact]
    public void QuietPodOnAQuietNode_HasNoSignal()
        => Assert.Empty(Classify(Pod(), Node()));

    public static TheoryData<string, PodFacts, NodeFacts?, SignalKind, SignalScope> Signals => new()
    {
        { "webhook annotation", Pod(evictionRequested: T), Node(), SignalKind.Eviction, SignalScope.Pod },
        { "eviction API", Pod(disruption: "EvictionByEvictionAPI"), Node(), SignalKind.Eviction, SignalScope.Pod },
        { "kubelet", Pod(disruption: "TerminationByKubelet"), Node(), SignalKind.KubeletTermination, SignalScope.Pod },
        { "preemption", Pod(disruption: "PreemptionByScheduler"), Node(), SignalKind.Disruption, SignalScope.Pod },
        { "deletion", Pod(deleting: T), Node(), SignalKind.Deletion, SignalScope.Pod },
        { "cordon", Pod(), Node(cordoned: true), SignalKind.Cordon, SignalScope.Node },
        { "karpenter", Pod(), Node(taints: ["karpenter.sh/disrupted"]), SignalKind.Taint, SignalScope.Node },
        { "out of service", Pod(), Node(taints: ["node.kubernetes.io/out-of-service"]), SignalKind.Taint, SignalScope.Node },
        { "shutdown", Pod(), Node(ready: "False", message: "node is shutting down"), SignalKind.NodeShutdown, SignalScope.Node },
    };

    [Theory]
    [MemberData(nameof(Signals))]
    public void EachSignal_HasItsKindAndScope(string _, PodFacts pod, NodeFacts? node, SignalKind kind, SignalScope scope)
        => Assert.Equal((kind, scope), Assert.Single(Classify(pod, node)));

    [Fact]
    public void AnEvictionSeenByTheWebhook_IsNotCountedTwice()
        => Assert.Single(Classify(Pod(evictionRequested: T, disruption: "EvictionByEvictionAPI"), Node()));

    [Fact]
    public void TheCordonTaint_IsNotASecondSignal()
        => Assert.Equal([(SignalKind.Cordon, SignalScope.Node)], Classify(Pod(), Node(cordoned: true, taints: ["node.kubernetes.io/unschedulable"])));

    [Theory]
    [InlineData("node.kubernetes.io/not-ready")]
    [InlineData("node.kubernetes.io/disk-pressure")]
    [InlineData("example.com/custom")]
    public void OtherTaints_AreNotMaintenance(string taint)
        => Assert.Empty(Classify(Pod(), Node(taints: [taint])));

    [Fact]
    public void ANodeNotReadyForAnotherReason_IsNotAShutdown()
        => Assert.Empty(Classify(Pod(), Node(ready: "False", message: "container runtime is down")));

    [Fact]
    public void TheNodeOfAnotherPod_SaysNothingAboutThisOne()
        => Assert.Empty(Classify(Pod(node: "n2"), Node(cordoned: true)));

    [Fact]
    public void ConfiguredTaints_AreSignals()
    {
        var signals = MaintenanceCore.Classify(Pod(), Node(taints: ["example.com/draining"]), new HashSet<string> { "example.com/draining" });
        Assert.Equal(SignalKind.Taint, Assert.Single(signals).Kind);
    }

    [Fact]
    public void SignalsAccumulate()
        => Assert.Equal(3, Classify(Pod(evictionRequested: T, deleting: T), Node(cordoned: true)).Count);
}
