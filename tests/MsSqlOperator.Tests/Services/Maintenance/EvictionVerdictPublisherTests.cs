using System.Text.Json;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Core.Maintenance;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Maintenance;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services.Maintenance;

/// <summary>The leader's verdicts, from the health pass and the signals.</summary>
public class EvictionVerdictPublisherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MaintenanceSignals _signals = new();
    private readonly FakeKubernetesClient _client = new();
    private readonly FakeEventPublisher _events = new();
    private readonly FakeTimeProvider _time = new();
    private readonly OperatorOptions _options = new() { NodeMaintenance = SwitchMode.Enforce };

    public EvictionVerdictPublisherTests() => _time.SetUtcNow(T0.AddSeconds(5));

    private (EvictionVerdictPublisher Publisher, MaintenanceObserver Observer) Create()
    {
        var observer = new MaintenanceObserver(_signals, _options, _events, new OperatorMetrics(), _time, NullLogger<MaintenanceObserver>.Instance);
        return (new EvictionVerdictPublisher(_signals, observer, _options, _client, _events, _time,
            NullLogger<EvictionVerdictPublisher>.Instance), observer);
    }

    private static SqlAvailabilityGroup Ag(Dictionary<string, string>? annotations = null) => new()
    {
        ApiVersion = "mssql-operator.evariops.com/v1alpha1",
        Kind = "SqlAvailabilityGroup",
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "prod", Uid = "u-ag", Annotations = annotations },
        Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
        Status = new SqlAvailabilityGroupStatus { Conditions = [] },
    };

    private static JsonElement Pod(string name, string node, int? requestedAt = null, int? lastRequestAt = null)
    {
        var annotations = requestedAt is { } at
            ? $$""","annotations":{"{{MaintenanceSignals.RequestedAtAnnotation}}":"{{EvictionAnnotations.Stamp(T0.AddSeconds(at))}}","{{MaintenanceSignals.RequestedByAnnotation}}":"admin","{{MaintenanceSignals.LastRequestAtAnnotation}}":"{{EvictionAnnotations.Stamp(T0.AddSeconds(lastRequestAt ?? at))}}"}"""
            : "";
        return JsonDocument.Parse("{\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"prod\"" + annotations
            + "},\"spec\":{\"nodeName\":\"" + node + "\"}}").RootElement;
    }

    private void Seed(params JsonElement[] pods)
    {
        _signals.ReplacePods("SqlAvailabilityGroup", pods.Select(p => ("prod/ag", p)).ToList());
        _signals.MarkPodsSeeded("Pod (ag)");
        _signals.MarkPodsSeeded("Pod (instance)");
        _signals.MarkNodesSeeded();
    }

    private static AgReplicaStateInfo Replica(string name, bool primary = false, bool failoverReady = true)
        => new(name, primary ? "PRIMARY" : "SECONDARY", "HEALTHY", "CONNECTED", "ONLINE", primary || failoverReady,
            AvailabilityMode: "SYNCHRONOUS_COMMIT");

    private static AgHealthReport Health(params AgReplicaStateInfo[] replicas)
        => new(true, "ag-0", replicas.ToList(),
            replicas.Select(r => new AgDatabaseStateInfo("app", r.Name, r.Role, "SYNCHRONIZED", true, 0, 0)).ToList(),
            [], replicas.Select(r => r.Name).ToList(), []);

    private static readonly AgHealthReport Healthy = Health(Replica("ag-0", primary: true), Replica("ag-1"), Replica("ag-2"));

    private async Task<SqlAvailabilityGroup> PassAsync(AgHealthReport? health, SqlAvailabilityGroup? ag = null)
    {
        ag ??= Ag();
        var (publisher, observer) = Create();
        ag.Status!.Maintenance = await observer.ObserveAsync(ag, ag.Status.Maintenance, ag.Status.Conditions!, CancellationToken.None);
        await publisher.PublishAsync(ag, health, null, CancellationToken.None);
        return ag;
    }

    [Fact]
    public async Task ASecondaryWhoseOthersAreSynchronized_GetsAnAllowVerdict_OnItsPod_AndInTheStatus()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0));

        var ag = await PassAsync(Healthy);

        var patch = Assert.Single(_client.PatchPodAnnotationsCalls);
        Assert.Equal("ag-2", patch.Name);
        var verdict = EvictionAnnotations.VerdictOf(new Dictionary<string, string> { [MaintenanceSignals.VerdictAnnotation] = patch.Annotations[MaintenanceSignals.VerdictAnnotation]! });
        Assert.Equal(VerdictDecision.Allow, verdict!.Decision);
        Assert.Equal(T0.AddSeconds(35), verdict.ValidUntil);
        var status = Assert.Single(ag.Status!.Maintenance!.Pods);
        Assert.Equal(("Allow", "admin", T0.AddSeconds(600).UtcDateTime), (status.Verdict, status.RequestedBy, status.Deadline));
        Assert.Single(_events.Calls, c => c.Reason == "EvictionAllowed");
    }

    [Fact]
    public async Task ASecondary_WaitsForAReplicaToResynchronize()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0));

        var ag = await PassAsync(Health(Replica("ag-0", primary: true), Replica("ag-1", failoverReady: false), Replica("ag-2")));

        var status = Assert.Single(ag.Status!.Maintenance!.Pods);
        Assert.Equal(("Delay", "waiting for ag-1 to be synchronized"), (status.Verdict, status.Reason));
        Assert.Single(_events.Calls, c => c.Reason == "EvictionDelayed");
    }

    [Fact]
    public async Task AnUnchangedVerdict_IsRewrittenOnlyWhenHalfItsValidityIsGone()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0));
        var (publisher, observer) = Create();
        var ag = Ag();
        async Task Pass()
        {
            ag.Status!.Maintenance = await observer.ObserveAsync(ag, ag.Status.Maintenance, ag.Status.Conditions!, CancellationToken.None);
            await publisher.PublishAsync(ag, Healthy, null, CancellationToken.None);
        }

        await Pass();
        _time.Advance(TimeSpan.FromSeconds(5));
        await Pass();
        Assert.Single(_client.PatchPodAnnotationsCalls);

        _time.Advance(TimeSpan.FromSeconds(11));
        await Pass();
        Assert.Equal(2, _client.PatchPodAnnotationsCalls.Count);
        Assert.Single(_events.Calls, c => c.Reason == "EvictionAllowed");
    }

    [Fact]
    public async Task InShadow_TheVerdictIsPublished_ButNeverWrittenOnThePod()
    {
        _options.NodeMaintenance = SwitchMode.Shadow;
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0));

        var ag = await PassAsync(Healthy);

        Assert.Empty(_client.PatchPodAnnotationsCalls);
        Assert.Equal("Allow", Assert.Single(ag.Status!.Maintenance!.Pods).Verdict);
        Assert.Single(_events.Calls, c => c.Reason == "EvictionAllowed" && c.Message.StartsWith("Shadow"));
    }

    [Fact]
    public async Task FromShadowToEnforce_TheVerdictIsWrittenAtOnce()
    {
        _options.NodeMaintenance = SwitchMode.Shadow;
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0));
        var (publisher, observer) = Create();
        var ag = Ag();
        ag.Status!.Maintenance = await observer.ObserveAsync(ag, null, ag.Status.Conditions!, CancellationToken.None);
        await publisher.PublishAsync(ag, Healthy, null, CancellationToken.None);

        _options.NodeMaintenance = SwitchMode.Enforce;
        _time.Advance(TimeSpan.FromSeconds(1));
        await publisher.PublishAsync(ag, Healthy, null, CancellationToken.None);

        Assert.Single(_client.PatchPodAnnotationsCalls);
        Assert.Single(_events.Calls, c => c.Reason == "EvictionAllowed");
    }

    [Fact]
    public async Task AnAbandonedRequest_IsCleared()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: -300, lastRequestAt: -200));

        await PassAsync(Healthy);

        var patch = Assert.Single(_client.PatchPodAnnotationsCalls);
        Assert.Equal("ag-2", patch.Name);
        Assert.All(MaintenanceSignals.RequestAnnotations, a => Assert.Null(patch.Annotations[a]));
    }

    [Fact]
    public async Task BeforeTheCachesAreSynchronized_NothingIsDecided()
    {
        _signals.ReplacePods("SqlAvailabilityGroup", [("prod/ag", Pod("ag-2", "n2", requestedAt: 0))]);

        var (publisher, _) = Create();
        Assert.False(await publisher.PublishAsync(Ag(), Healthy, null, CancellationToken.None));
        Assert.Empty(_client.PatchPodAnnotationsCalls);
    }

    [Fact]
    public async Task AnAgNotRunningYet_HoldsNothing()
    {
        Seed(Pod("ag-0", "n0", requestedAt: 0));

        var ag = await PassAsync(health: null);

        Assert.Equal("Allow", Assert.Single(ag.Status!.Maintenance!.Pods).Verdict);
    }

    [Fact]
    public async Task TheReleaseAnnotation_IsRemoved_OnceNoRequestIsLeft()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2"));

        await PassAsync(Healthy, Ag(new Dictionary<string, string> { [MaintenanceObserver.ReleaseAnnotation] = "now" }));

        var (_, annotations) = Assert.Single(_client.PatchMetadataCalls);
        Assert.Null(annotations[MaintenanceObserver.ReleaseAnnotation]);
    }

    private static ReplicaView View(string pod, bool ready = true) => new(pod, "n", pod == "ag-0", ready, true, true, false);

    private static (string, Verdict) V(string pod, VerdictDecision decision, int deadlineSeconds = 600)
        => (pod, new Verdict(decision, "", T0.AddSeconds(deadlineSeconds)));

    [Fact]
    public void ThePdbBudget_LetsGoExactlyTheReadyReplicaTheWebhookReleases()
    {
        IReadOnlyList<ReplicaView> oneAway = [View("ag-0"), View("ag-1", ready: false), View("ag-2")];
        var now = T0.AddSeconds(5);

        // ag-2 allowed while ag-1 is away: maxUnavailable 1 would retain it.
        Assert.Equal(2, EvictionVerdictPublisher.Budget(oneAway, [V("ag-2", VerdictDecision.Allow)], now));
        // Delayed: the PDB stays as it is.
        Assert.Equal(1, EvictionVerdictPublisher.Budget(oneAway, [V("ag-2", VerdictDecision.Delay)], now));
        // Delayed past its deadline: the webhook lets it go, so must the PDB.
        Assert.Equal(2, EvictionVerdictPublisher.Budget(oneAway, [V("ag-2", VerdictDecision.Delay, deadlineSeconds: 4)], now));
        // A replica that is not ready leaves through AlwaysAllow: nothing to widen.
        Assert.Equal(1, EvictionVerdictPublisher.Budget(oneAway, [V("ag-1", VerdictDecision.Allow)], now));
    }

    [Fact]
    public async Task InEnforce_ThePdbIsWidened_OnceAndBackToOne_WhenNoRequestIsLeft()
    {
        // ag-2 asked 601 s ago (its drain retries), ag-1 is away (not reachable): past the deadline.
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0, lastRequestAt: 595));
        var (publisher, observer) = Create();
        var ag = Ag();
        _time.SetUtcNow(T0.AddSeconds(601));
        var oneAway = Healthy with { ReachableReplicas = ["ag-0", "ag-2"] };
        ag.Status!.Maintenance = await observer.ObserveAsync(ag, null, ag.Status.Conditions!, CancellationToken.None);
        await publisher.PublishAsync(ag, oneAway, null, CancellationToken.None);

        Assert.True(publisher.TakePdbChange(ag, out var widened));
        Assert.Equal(2, widened);
        Assert.False(publisher.TakePdbChange(ag, out _));

        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2"));
        await publisher.PublishAsync(ag, Healthy, null, CancellationToken.None);

        Assert.True(publisher.TakePdbChange(ag, out var restored));
        Assert.Equal(1, restored);
    }

    [Fact]
    public async Task InShadow_ThePdbIsNeverWidened()
    {
        _options.NodeMaintenance = SwitchMode.Shadow;
        _time.SetUtcNow(T0.AddSeconds(601));
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2", requestedAt: 0, lastRequestAt: 595));

        await PassAsync(Healthy with { ReachableReplicas = ["ag-0", "ag-2"] });

        Assert.False(Create().Publisher.TakePdbChange(Ag(), out _));
    }

    [Fact]
    public async Task TheAgPdb_LetsAReplicaThatIsNotReadyGo()
    {
        var client = new FakeKubernetesClient();

        await MsSqlOperator.Services.Kubernetes.OwnedChildren.ApplyPodDisruptionBudgetAsync(client, NullLogger.Instance, Ag(),
            new Dictionary<string, string> { ["mssql-operator.evariops.com/ag"] = "ag" }, 2, CancellationToken.None,
            unhealthyPodEvictionPolicy: "AlwaysAllow");

        var pdb = Assert.Single(client.Applied<V1PodDisruptionBudget>());
        Assert.Equal(("AlwaysAllow", "2"), (pdb.Spec.UnhealthyPodEvictionPolicy, pdb.Spec.MaxUnavailable.Value));
    }

    [Fact]
    public void ReplicaViews_ComeFromTheHealthPass()
    {
        Seed(Pod("ag-0", "n0"), Pod("ag-1", "n1"), Pod("ag-2", "n2"));
        var health = Health(Replica("ag-0", primary: true), Replica("ag-1"), Replica("ag-2")) with
        {
            // ag-2 holds no copy of app yet: it is seeding.
            DatabaseStates = [new("app", "ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0), new("app", "ag-1", "SECONDARY", "SYNCHRONIZED", true, 0, 0)],
            ReachableReplicas = ["ag-0", "ag-2"],
        };

        var replicas = Create().Publisher.ReplicasOf(Ag(), health);

        Assert.Equal([true, false, false], replicas.Select(r => r.Primary));
        Assert.Equal([true, false, true], replicas.Select(r => r.Ready));
        Assert.Equal([false, false, true], replicas.Select(r => r.Seeding));
        Assert.Equal(["n0", "n1", "n2"], replicas.Select(r => r.Node));
    }
}
