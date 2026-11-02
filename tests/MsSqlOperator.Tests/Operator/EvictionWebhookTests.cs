using System.Text.Json;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Core.Maintenance;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Services.Maintenance;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Operator;

/// <summary>The pods/eviction webhook delays the replicas of an AG within a deadline, and
/// allows everything it cannot decide.</summary>
public class EvictionWebhookTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeKubernetesClient _client = new();
    private readonly FakeEventPublisher _events = new();
    private readonly FakeTimeProvider _time = new();
    private readonly OperatorOptions _options = new() { NodeMaintenance = SwitchMode.Enforce };

    public EvictionWebhookTests()
    {
        _time.SetUtcNow(T0);
        _client.SetGet("ag", "prod", Ag());
    }

    private EvictionWebhook Webhook()
    {
        var observer = new MaintenanceObserver(new MaintenanceSignals(), _options, _events, new OperatorMetrics(), _time,
            NullLogger<MaintenanceObserver>.Instance);
        return new EvictionWebhook(_client, _options, observer, _events, new OperatorMetrics(), _time, NullLogger<EvictionWebhook>.Instance);
    }

    private static SqlAvailabilityGroup Ag(Dictionary<string, string>? annotations = null) => new()
    {
        ApiVersion = "mssql-operator.evariops.com/v1alpha1",
        Kind = "SqlAvailabilityGroup",
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "prod", Uid = "u-ag", Annotations = annotations },
        Spec = new SqlAvailabilityGroupSpec(),
    };

    private void SetPod(string name = "ag-1", Dictionary<string, string>? annotations = null, bool managed = true)
        => _client.SetGet(name, "prod", new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = "prod",
                // As the StatefulSet labels a SQL pod: the identity label of its owner, no managed-by.
                Labels = managed
                    ? new Dictionary<string, string> { [OperatorLabels.Ag] = "ag", ["mssql-operator.evariops.com/ag-role"] = "secondary" }
                    : new Dictionary<string, string> { ["app"] = "web", [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                Annotations = annotations,
            },
        });

    /// <summary>A request first seen at <paramref name="atSeconds"/>, repeated by its drain until 5 s ago.</summary>
    private static Dictionary<string, string> Requested(int atSeconds, string by = "admin", string? verdict = null)
    {
        var annotations = new Dictionary<string, string>
        {
            [MaintenanceSignals.RequestedAtAnnotation] = EvictionAnnotations.Stamp(T0.AddSeconds(atSeconds)),
            [MaintenanceSignals.RequestedByAnnotation] = by,
            [MaintenanceSignals.LastRequestAtAnnotation] = EvictionAnnotations.Stamp(T0.AddSeconds(Math.Max(atSeconds, -5))),
        };
        if (verdict is not null)
            annotations[MaintenanceSignals.VerdictAnnotation] = verdict;
        return annotations;
    }

    private static string VerdictJson(VerdictDecision decision, int validUntilSeconds)
        => EvictionAnnotations.Serialize(new PublishedVerdict(decision, "waiting for ag-0 to be synchronized",
            T0.AddSeconds(600), T0.AddSeconds(validUntilSeconds)));

    private static AdmissionReview Eviction(string pod = "ag-1", string user = "admin", bool dryRun = false, bool deleteOptionsDryRun = false)
        => new()
        {
            Request = new AdmissionRequest
            {
                Uid = "req-1",
                Operation = "CREATE",
                Name = pod,
                Namespace = "prod",
                SubResource = "eviction",
                DryRun = dryRun,
                UserInfo = new AdmissionUserInfo { Username = user },
                Object = JsonDocument.Parse(
                    $$"""{"apiVersion":"policy/v1","kind":"Eviction","metadata":{"name":"{{pod}}","namespace":"prod"},"deleteOptions":{{(deleteOptionsDryRun ? """{"dryRun":["All"]}""" : "{}")}}}""").RootElement,
            },
        };

    [Fact]
    public async Task APodTheOperatorDoesNotManage_IsAllowed_WithoutAnyOtherCall()
    {
        SetPod("web-1", managed: false);

        var review = await Webhook().ReviewAsync(Eviction("web-1"), CancellationToken.None);

        Assert.True(review.Response!.Allowed);
        Assert.Equal("req-1", review.Response.Uid);
        Assert.Single(_client.GetCalls);
        Assert.Empty(_client.PatchPodAnnotationsCalls);
    }

    [Fact]
    public async Task OffGlobally_NothingIsRead()
    {
        _options.NodeMaintenance = SwitchMode.Off;
        SetPod();

        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
        Assert.Empty(_client.GetCalls);
    }

    [Fact]
    public async Task AFirstRequest_IsRecorded_AndDelayed_UntilTheLeaderHasAVerdict()
    {
        SetPod();

        var review = await Webhook().ReviewAsync(Eviction(user: "system:node:w-1"), CancellationToken.None);

        Assert.False(review.Response!.Allowed);
        Assert.Equal(429, review.Response.Status!.Code);
        Assert.Equal("mssql-operator, availability group prod/ag: waiting for the operator's verdict; delayed 0 s of 210 s at most.",
            review.Response.Status.Message);
        var patch = Assert.Single(_client.PatchPodAnnotationsCalls);
        Assert.Equal("system:node:w-1", patch.Annotations[MaintenanceSignals.RequestedByAnnotation]);
        Assert.Equal(EvictionAnnotations.Stamp(T0), patch.Annotations[MaintenanceSignals.RequestedAtAnnotation]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)] // kubectl drain --dry-run=server: only the Eviction says it (VM-02)
    public async Task ADryRun_WritesNothing(bool requestDryRun, bool deleteOptionsDryRun)
    {
        SetPod();

        var review = await Webhook().ReviewAsync(Eviction(dryRun: requestDryRun, deleteOptionsDryRun: deleteOptionsDryRun), CancellationToken.None);

        Assert.False(review.Response!.Allowed);
        Assert.Empty(_client.PatchPodAnnotationsCalls);
        Assert.Empty(_events.Calls);
    }

    [Fact]
    public async Task AValidVerdict_IsFollowed_AndAStaleOneIsNot()
    {
        SetPod(annotations: Requested(-20, verdict: VerdictJson(VerdictDecision.Allow, 10)));
        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);

        SetPod(annotations: Requested(-20, verdict: VerdictJson(VerdictDecision.Delay, 10)));
        var delayed = (await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!;
        Assert.Equal("mssql-operator, availability group prod/ag: waiting for ag-0 to be synchronized; delayed 20 s of 600 s at most.",
            delayed.Status!.Message);

        SetPod(annotations: Requested(-20, verdict: VerdictJson(VerdictDecision.Allow, -1)));
        Assert.False((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
    }

    [Fact]
    public async Task TheDeadlineReached_TheEvictionGoesOn_WithAnEvent()
    {
        SetPod(annotations: Requested(-600, verdict: VerdictJson(VerdictDecision.Delay, 30)));

        var review = await Webhook().ReviewAsync(Eviction(), CancellationToken.None);

        Assert.True(review.Response!.Allowed);
        Assert.Single(_events.Calls, c => c.Reason == "MaintenanceDelayExpired" && c.Type == "Warning");
    }

    [Fact]
    public async Task TheReleaseAnnotation_LetsTheEvictionGo()
    {
        _client.SetGet("ag", "prod", Ag(new Dictionary<string, string> { [MaintenanceObserver.ReleaseAnnotation] = "now" }));
        SetPod(annotations: Requested(-20, verdict: VerdictJson(VerdictDecision.Delay, 30)));

        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
    }

    [Fact]
    public async Task InShadow_TheRequestIsRecorded_AndTheEvictionAllowed()
    {
        _options.NodeMaintenance = SwitchMode.Shadow;
        SetPod();

        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
        Assert.Single(_client.PatchPodAnnotationsCalls);
    }

    [Fact]
    public async Task AResourceAnnotatedShadow_IsNotHeld_UnderAnEnforceOperator()
    {
        _client.SetGet("ag", "prod", Ag(new Dictionary<string, string> { [MaintenanceObserver.ModeAnnotation] = "Shadow" }));
        SetPod();

        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
    }

    [Fact]
    public async Task ARequestThatCannotBeRecorded_IsAllowed()
    {
        _client.PatchPodAnnotationsFailure = new InvalidOperationException("forbidden");
        SetPod();

        Assert.True((await Webhook().ReviewAsync(Eviction(), CancellationToken.None)).Response!.Allowed);
    }

    [Fact]
    public async Task AnApiServerThatDoesNotAnswer_IsAllowed_WithinTheBudget()
    {
        _client.GetHangs = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var review = await Webhook().ReviewAsync(Eviction(), CancellationToken.None);

        Assert.True(review.Response!.Allowed);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task AnInstancePod_IsRecorded_ButNeverHeld()
    {
        _client.SetGet("sql", "prod", new SqlInstance
        {
            ApiVersion = "mssql-operator.evariops.com/v1alpha1",
            Kind = "SqlInstance",
            Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "prod", Uid = "u-sql" },
            Spec = new SqlInstanceSpec(),
        });
        _client.SetGet("sql-0", "prod", new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "sql-0",
                NamespaceProperty = "prod",
                Labels = new Dictionary<string, string> { [OperatorLabels.Instance] = "sql" },
            },
        });

        Assert.True((await Webhook().ReviewAsync(Eviction("sql-0"), CancellationToken.None)).Response!.Allowed);
        Assert.Single(_client.PatchPodAnnotationsCalls);
    }
}
