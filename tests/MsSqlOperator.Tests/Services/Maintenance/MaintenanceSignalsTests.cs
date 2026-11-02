using System.Text.Json;
using MsSqlOperator.Core.Maintenance;
using MsSqlOperator.Services.Maintenance;

namespace MsSqlOperator.Tests.Services.Maintenance;

/// <summary>The facts kept from the pod and node watches.</summary>
public class MaintenanceSignalsTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static JsonElement Pod(string name, string node, string extraMeta = "", string conditions = "")
        => Json("{\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"prod\"" + extraMeta + "},\"spec\":{\"nodeName\":\"" + node +
                "\"},\"status\":{\"phase\":\"Running\",\"conditions\":[" + conditions + "]}}");

    private static JsonElement Node(string name, bool cordoned = false, string taints = "", string ready = "True", string message = "kubelet is posting ready status")
        => Json("{\"metadata\":{\"name\":\"" + name + "\"},\"spec\":{" + (cordoned ? "\"unschedulable\":true," : "") + "\"taints\":[" + taints +
                "]},\"status\":{\"conditions\":[{\"type\":\"Ready\",\"status\":\"" + ready + "\",\"message\":\"" + message +
                "\",\"lastTransitionTime\":\"2026-11-02T12:00:00Z\"}]}}");

    private static MaintenanceSignals Synced(params (string Owner, JsonElement Pod)[] pods)
    {
        var signals = new MaintenanceSignals();
        signals.ReplacePods("SqlAvailabilityGroup", pods.Select(p => (p.Owner, p.Pod)).ToList());
        signals.MarkPodsSeeded("Pod (ag)");
        signals.MarkPodsSeeded("Pod (instance)");
        signals.MarkNodesSeeded();
        return signals;
    }

    [Fact]
    public void PodFacts_ReadTheDisruptionTarget_TheDeletion_AndTheWebhookAnnotation()
    {
        var facts = MaintenanceSignals.PodFactsOf(Pod("ag-0", "n1",
            ",\"deletionTimestamp\":\"2026-11-02T12:00:05Z\",\"annotations\":{\"mssql-operator.evariops.com/disruption-requested-at\":\"2026-11-02T12:00:01Z\"}",
            "{\"type\":\"DisruptionTarget\",\"status\":\"True\",\"reason\":\"TerminationByKubelet\",\"lastTransitionTime\":\"2026-11-02T12:00:02Z\"}"));

        Assert.Equal("n1", facts.Node);
        Assert.Equal("TerminationByKubelet", facts.DisruptionTargetReason);
        Assert.NotNull(facts.DeletionTimestamp);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 1, TimeSpan.Zero), facts.EvictionRequestedAt);
    }

    [Fact]
    public void ADisruptionTargetThatIsFalse_IsIgnored()
        => Assert.Null(MaintenanceSignals.PodFactsOf(Pod("ag-0", "n1", conditions: "{\"type\":\"DisruptionTarget\",\"status\":\"False\",\"reason\":\"X\"}")).DisruptionTargetReason);

    [Fact]
    public void NodeFacts_ReadTheCordon_TheTaints_AndTheShutdown()
    {
        var facts = MaintenanceSignals.NodeFactsOf(Node("n1", cordoned: true, taints: "{\"key\":\"karpenter.sh/disrupted\",\"effect\":\"NoSchedule\"}",
            ready: "False", message: "node is shutting down"))!;

        Assert.True(facts.Unschedulable);
        Assert.Equal(["karpenter.sh/disrupted"], facts.TaintKeys);
        Assert.True(MaintenanceCore.IsShuttingDown(facts));
    }

    [Fact]
    public void For_JoinsThePodsOfTheOwner_WithTheirNode()
    {
        var signals = Synced(("prod/ag", Pod("ag-0", "n1")), ("prod/ag", Pod("ag-1", "n2")), ("prod/other", Pod("other-0", "n1")));
        signals.ObserveNode(Node("n1", cordoned: true));
        signals.ObserveNode(Node("n2"));

        var pods = signals.For("SqlAvailabilityGroup", "prod/ag");

        var pod = Assert.Single(pods);
        Assert.Equal("ag-0", pod.Pod);
        Assert.Equal(SignalKind.Cordon, Assert.Single(pod.Signals).Kind);
    }

    [Fact]
    public void ObserveNode_ReportsOnlyAChangeOfSignals()
    {
        var signals = Synced();
        Assert.False(signals.ObserveNode(Node("n1")));                                  // new, quiet
        Assert.False(signals.ObserveNode(Node("n1", message: "heartbeat")));            // status churn
        Assert.True(signals.ObserveNode(Node("n1", cordoned: true)));                   // cordon
        Assert.False(signals.ObserveNode(Node("n1", cordoned: true)));
        Assert.True(signals.ObserveNode(Node("n1")));                                   // uncordon
        Assert.True(signals.ObserveNode(Node("n2", ready: "False", message: "node is shutting down"))); // new, signalling
    }

    [Fact]
    public void OwnersOn_ListsTheResourcesOfTheNode()
    {
        var signals = Synced(("prod/ag", Pod("ag-0", "n1")), ("prod/ag", Pod("ag-1", "n1")), ("prod/b", Pod("b-0", "n2")));

        Assert.Equal([("SqlAvailabilityGroup", "prod/ag")], signals.OwnersOn("n1"));
    }

    [Fact]
    public void ARelist_ForgetsThePodsItNoLongerLists()
    {
        var signals = Synced(("prod/ag", Pod("ag-0", "n1")));
        signals.ReplacePods("SqlAvailabilityGroup", []);

        Assert.Empty(signals.OwnersOn("n1"));
    }

    [Fact]
    public void IsSynced_OnlyOnceThePodsOfEveryKindAndTheNodesWereListed()
    {
        var signals = new MaintenanceSignals();
        signals.MarkPodsSeeded("Pod (ag)");
        signals.MarkNodesSeeded();
        Assert.False(signals.IsSynced);
        signals.MarkPodsSeeded("Pod (instance)");
        Assert.True(signals.IsSynced);
    }
}
