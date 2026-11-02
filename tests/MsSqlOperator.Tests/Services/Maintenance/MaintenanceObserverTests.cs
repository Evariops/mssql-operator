using System.Text.Json;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Maintenance;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services.Maintenance;

/// <summary>status.maintenance, the NodeMaintenance condition and the events, in Shadow.</summary>
public class MaintenanceObserverTests
{
    private readonly MaintenanceSignals _signals = new();
    private readonly FakeEventPublisher _events = new();
    private readonly FakeTimeProvider _time = new();
    private readonly OperatorOptions _options = new();

    public MaintenanceObserverTests() => _time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private MaintenanceObserver Observer() => new(_signals, _options, _events, new OperatorMetrics(), _time, NullLogger<MaintenanceObserver>.Instance);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static SqlAvailabilityGroup Ag(Dictionary<string, string>? annotations = null) => new()
    {
        ApiVersion = "mssql-operator.evariops.com/v1alpha1",
        Kind = "SqlAvailabilityGroup",
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "prod", Uid = "u", Annotations = annotations },
        Spec = new SqlAvailabilityGroupSpec(),
        Status = new SqlAvailabilityGroupStatus { Conditions = [] },
    };

    private void Seed(bool cordoned)
    {
        _signals.ReplacePods("SqlAvailabilityGroup",
        [
            ("prod/ag", Json("""{"metadata":{"name":"ag-0","namespace":"prod"},"spec":{"nodeName":"n1"}}""")),
            ("prod/ag", Json("""{"metadata":{"name":"ag-1","namespace":"prod"},"spec":{"nodeName":"n2"}}""")),
        ]);
        _signals.ObserveNode(Json("{\"metadata\":{\"name\":\"n1\"},\"spec\":{" + (cordoned ? "\"unschedulable\":true" : "") + "}}"));
        _signals.MarkPodsSeeded("Pod (ag)");
        _signals.MarkPodsSeeded("Pod (instance)");
        _signals.MarkNodesSeeded();
    }

    [Fact]
    public async Task NoSignal_OnAResourceThatNeverHadOne_WritesNothing()
    {
        Seed(cordoned: false);
        var ag = Ag();

        var status = await Observer().ObserveAsync(ag, null, ag.Status!.Conditions!, CancellationToken.None);

        Assert.Null(status);
        Assert.Empty(ag.Status.Conditions!);
        Assert.Empty(_events.Calls);
    }

    [Fact]
    public async Task ACordon_IsPublished_InShadow_AndTheSecondPassAddsNothing()
    {
        Seed(cordoned: true);
        var ag = Ag();

        var first = await Observer().ObserveAsync(ag, null, ag.Status!.Conditions!, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        var second = await Observer().ObserveAsync(ag, first, ag.Status.Conditions!, CancellationToken.None);

        var pod = Assert.Single(second!.Pods);
        Assert.Equal(("ag-0", "n1", "Cordon"), (pod.Pod, pod.Node, Assert.Single(pod.Signals)));
        Assert.Equal(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pod.Since); // kept from the first pass
        Assert.Equal("Shadow", second.Mode);
        var condition = Assert.Single(ag.Status.Conditions!);
        Assert.Equal(("NodeMaintenance", "True", "SignalsObserved"), (condition.Type, condition.Status, condition.Reason));
        Assert.Single(_events.Calls, c => c.Reason == "MaintenanceSignal" && c.Message.Contains("Shadow"));
    }

    [Fact]
    public async Task TheEndOfTheSignals_ClearsTheStatus_AndSaysIt()
    {
        Seed(cordoned: true);
        var ag = Ag();
        var during = await Observer().ObserveAsync(ag, null, ag.Status!.Conditions!, CancellationToken.None);
        _signals.ObserveNode(Json("""{"metadata":{"name":"n1"},"spec":{}}"""));

        var after = await Observer().ObserveAsync(ag, during, ag.Status.Conditions!, CancellationToken.None);

        Assert.Null(after);
        Assert.Equal("False", Assert.Single(ag.Status.Conditions!).Status);
        Assert.Contains("MaintenanceEnded", _events.Reasons);
    }

    [Fact]
    public async Task BeforeTheWatchesListed_NothingIsConcluded()
    {
        // A restarted leader must not clear a maintenance in progress on its first pass.
        var ag = Ag();
        var current = new MaintenanceStatus { Mode = "Shadow", Pods = [new MaintenancePodStatus { Pod = "ag-0", Signals = ["Cordon"] }] };

        var status = await Observer().ObserveAsync(ag, current, ag.Status!.Conditions!, CancellationToken.None);

        Assert.Same(current, status);
        Assert.Empty(_events.Calls);
    }

    [Theory]
    [InlineData(SwitchMode.Off, null)]
    [InlineData(SwitchMode.Shadow, "Off")]
    public async Task Off_GlobalOrByAnnotation_ObservesNothing(SwitchMode global, string? annotation)
    {
        _options.NodeMaintenance = global;
        Seed(cordoned: true);
        var ag = Ag(annotation is null ? null : new Dictionary<string, string> { [MaintenanceObserver.ModeAnnotation] = annotation });

        var status = await Observer().ObserveAsync(ag, null, ag.Status!.Conditions!, CancellationToken.None);

        Assert.Null(status);
        Assert.Empty(_events.Calls);
    }

    [Fact]
    public void TheAnnotation_CannotRaiseTheModeAboveTheOperators()
    {
        var ag = Ag(new Dictionary<string, string> { [MaintenanceObserver.ModeAnnotation] = "Enforce" });

        Assert.Equal(SwitchMode.Shadow, Observer().ModeOf(ag));
    }
}
