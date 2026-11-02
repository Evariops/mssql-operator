using MsSqlOperator.Core.Maintenance;

namespace MsSqlOperator.Tests.Core;

/// <summary>Delay an eviction only while waiting buys a copy or a lossless failover, never past
/// the deadline.</summary>
public class EvictionVerdictsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static ReplicaView R(string pod, string node, bool primary = false, bool ready = true, bool synchronized = true,
        bool synchronous = true, bool seeding = false)
        => new(pod, node, primary, ready, synchronized, synchronous, seeding);

    private static readonly IReadOnlyList<ReplicaView> Healthy =
        [R("ag-0", "n0", primary: true), R("ag-1", "n1"), R("ag-2", "n2")];

    private static EvictionRequest Req(string pod, int atSeconds = 0, bool fromNode = false) => new(pod, T0.AddSeconds(atSeconds), fromNode);

    private static Dictionary<string, IReadOnlyList<MaintenanceSignal>> Signals(params (string Pod, SignalKind Kind)[] signals)
        => signals.GroupBy(s => s.Pod).ToDictionary(g => g.Key,
            g => (IReadOnlyList<MaintenanceSignal>)g.Select(s => new MaintenanceSignal(s.Kind,
                s.Kind is SignalKind.Cordon or SignalKind.Taint or SignalKind.NodeShutdown ? SignalScope.Node : SignalScope.Pod, "", null)).ToList());

    private static Verdict Decide(EvictionRequest request, IReadOnlyList<ReplicaView>? replicas = null,
        Dictionary<string, IReadOnlyList<MaintenanceSignal>>? signals = null, EvictionRequest[]? requests = null,
        string? operation = null, int nowSeconds = 1)
        => EvictionVerdicts.Decide(request, replicas ?? Healthy, signals ?? Signals((request.Pod, SignalKind.Eviction)),
            requests ?? [request], operation, T0.AddSeconds(nowSeconds), EvictionBudget.Default);

    [Fact]
    public void ASecondary_WhoseOthersAreSynchronized_Leaves()
        => Assert.Equal(VerdictDecision.Allow, Decide(Req("ag-2")).Decision);

    [Fact]
    public void ASecondary_WaitsForAReplicaThatIsNotBack()
    {
        var verdict = Decide(Req("ag-2"), [R("ag-0", "n0", primary: true), R("ag-1", "n1", ready: false), R("ag-2", "n2")]);

        Assert.Equal(VerdictDecision.Delay, verdict.Decision);
        Assert.Equal("waiting for ag-1 to be back", verdict.Reason);
    }

    [Fact]
    public void ASecondary_WaitsForASynchronousReplicaToResynchronize_NotForAnAsynchronousOne()
    {
        Assert.Equal("waiting for ag-1 to be synchronized",
            Decide(Req("ag-2"), [R("ag-0", "n0", primary: true), R("ag-1", "n1", synchronized: false), R("ag-2", "n2")]).Reason);
        Assert.Equal(VerdictDecision.Allow,
            Decide(Req("ag-2"), [R("ag-0", "n0", primary: true), R("ag-1", "n1", synchronized: false, synchronous: false), R("ag-2", "n2")]).Decision);
    }

    [Fact]
    public void ASecondary_WaitsDuringADisruptiveOperation()
        => Assert.Equal("rolling update in progress on the availability group", Decide(Req("ag-2"), operation: "rolling update").Reason);

    [Fact]
    public void ThePrimary_WaitsForAPlannedFailover_ToATargetThatIsNotLeaving()
    {
        var signals = Signals(("ag-0", SignalKind.Eviction), ("ag-1", SignalKind.Cordon));

        var verdict = Decide(Req("ag-0"), signals: signals);

        Assert.Equal(VerdictDecision.Delay, verdict.Decision);
        Assert.Equal("planned failover to ag-2 before the primary leaves", verdict.Reason);
    }

    [Fact]
    public void ThePrimary_WithoutALosslessTarget_WaitsForTheDeadline()
    {
        var verdict = Decide(Req("ag-0"), [R("ag-0", "n0", primary: true), R("ag-1", "n1", synchronized: false), R("ag-2", "n2", synchronous: false)]);

        Assert.Equal(VerdictDecision.Delay, verdict.Decision);
        Assert.StartsWith("no replica can take over without loss", verdict.Reason);
    }

    [Theory]
    [InlineData(true, 210)]
    [InlineData(false, 600)]
    public void PastTheDeadline_EverythingIsAllowed(bool fromNode, int deadlineSeconds)
    {
        var request = Req("ag-0", fromNode: fromNode);

        Assert.Equal(VerdictDecision.Delay, Decide(request, nowSeconds: deadlineSeconds - 1).Decision);
        var verdict = Decide(request, nowSeconds: deadlineSeconds);
        Assert.Equal((VerdictDecision.Allow, "deadline reached"), (verdict.Decision, verdict.Reason));
        Assert.Equal(T0.AddSeconds(deadlineSeconds), verdict.Deadline);
    }

    [Fact]
    public void TwoDrainsAtOnce_TheOldestRequestGoesFirst_TheOtherWaits()
    {
        // Two secondaries drained in parallel: without an order, each waited for the other until both
        // deadlines, then both left together.
        var first = Req("ag-1", atSeconds: 0);
        var second = Req("ag-2", atSeconds: 2);
        var signals = Signals(("ag-1", SignalKind.Eviction), ("ag-2", SignalKind.Eviction));

        Assert.Equal(VerdictDecision.Allow, Decide(first, signals: signals, requests: [first, second], nowSeconds: 3).Decision);
        var waiting = Decide(second, signals: signals, requests: [first, second], nowSeconds: 3);
        Assert.Equal((VerdictDecision.Delay, "ag-1 leaves first"), (waiting.Decision, waiting.Reason));
    }

    [Fact]
    public void ARequestAtTheSameInstant_IsOrderedByPodName()
    {
        var a = Req("ag-1");
        var b = Req("ag-2");
        var signals = Signals(("ag-1", SignalKind.Eviction), ("ag-2", SignalKind.Eviction));

        Assert.Equal(VerdictDecision.Allow, Decide(a, signals: signals, requests: [a, b]).Decision);
        Assert.Equal(VerdictDecision.Delay, Decide(b, signals: signals, requests: [a, b]).Decision);
    }

    [Fact]
    public void ASecondary_WaitsWhileAnotherReplicaIsBeingDeleted()
        => Assert.Equal("ag-1 leaves first",
            Decide(Req("ag-2"), signals: Signals(("ag-2", SignalKind.Eviction), ("ag-1", SignalKind.Deletion))).Reason);

    [Fact]
    public void EveryReplicaOnStoppingNodes_TheAgStopsAnyway_AndNothingWaits()
    {
        // Co-located replicas (one node), or every node of the AG cordoned: waiting buys nothing.
        var colocated = new[] { R("ag-0", "n0", primary: true), R("ag-1", "n0"), R("ag-2", "n0") };
        var signals = Signals(("ag-0", SignalKind.Cordon), ("ag-1", SignalKind.Cordon), ("ag-2", SignalKind.Cordon), ("ag-0", SignalKind.Eviction));

        Assert.Equal(VerdictDecision.Allow, Decide(Req("ag-0"), colocated, signals).Decision);
    }

    [Fact]
    public void APodThatIsNotAReplica_IsAllowed()
        => Assert.Equal(VerdictDecision.Allow, Decide(Req("ag-9")).Decision);
}
