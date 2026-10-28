using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Runtime;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Operator;

/// <summary>Queue ages, liveness, freshness and watch metrics.</summary>
public class ObservabilityTests
{
    [Fact]
    public async Task WorkQueue_SeparatesTheWaitingFromTheInFlight()
    {
        var clock = new FakeTimeProvider();
        using var queue = new WorkQueue(clock);
        queue.Add("a");
        queue.Add("b");
        clock.Advance(TimeSpan.FromSeconds(10));

        var key = await queue.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(40, queue.OldestAgeSeconds, 1);        // "b" still waits
        Assert.Equal(30, queue.InFlightOldestAgeSeconds, 1); // "a" in its pass
        queue.Done(key, resetBackoff: true);
        Assert.Equal(0, queue.InFlightOldestAgeSeconds);
    }

    [Fact]
    public async Task RuntimeLiveness_FailsOnlyPastTheThreshold()
    {
        var waiting = 10.0;
        var liveness = new RuntimeLiveness();
        liveness.Register("SqlAvailabilityGroup", () => waiting, TimeSpan.FromMinutes(5));
        var check = new RuntimeLivenessHealthCheck(liveness);

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, (await check.CheckHealthAsync(new())).Status);
        waiting = 301;
        var unhealthy = await check.CheckHealthAsync(new());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Contains("SqlAvailabilityGroup", unhealthy.Description);
    }

    [Fact]
    public void OperationsInProgress_ReadFromTheStatus()
    {
        var started = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
        var status = new SqlAvailabilityGroupStatus
        {
            FailoverIntent = new AgLastFailoverStatus { Timestamp = started },
            Fencing = new AgFencingStatus { Pod = "ag-0", StartedAt = started },
            RollingUpdate = new AgRollingUpdateStatus { Phase = RollingUpdatePhases.Idle },
        };

        var operations = SqlAvailabilityGroupController.OperationsInProgress(status);

        Assert.Equal(["failover", "fencing"], operations.Keys.Order().ToList());
        Assert.Equal(started, operations["failover"].UtcDateTime);
    }

    [Fact]
    public async Task ResourceWatcher_ReportsTheLagOfEachEvent()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 28, 8, 0, 30, TimeSpan.Zero));
        var metrics = new RecordingMetrics();
        using var cts = new CancellationTokenSource();
        var element = JsonDocument.Parse("""
            {"metadata":{"name":"ag","managedFields":[{"time":"2026-10-28T08:00:00Z"},{"time":"2026-10-28T08:00:20Z"}]}}
            """).RootElement;
        var watcher = new ResourceWatcher("SqlAvailabilityGroup", NullLogger.Instance,
            _ => Task.FromResult("""{"items":[],"metadata":{"resourceVersion":"1"}}"""),
            (_, _, onEvent, _) =>
            {
                onEvent("MODIFIED", element);
                cts.Cancel();
                return Task.FromResult<string?>("2");
            },
            _ => { }, (_, _) => { }, clock, metrics: metrics);

        await watcher.RunAsync(cts.Token);

        Assert.Equal(("SqlAvailabilityGroup", (double?)10), metrics.Events.First());
    }

    private sealed class RecordingMetrics : IReconcileMetrics
    {
        public readonly List<(string Source, double? Lag)> Events = [];
        public void RecordReconciliation(string controller, double durationSeconds, bool success) { }
        public void RegisterQueue(string kind, Func<(int Depth, double OldestAgeSeconds)> probe) { }
        public void SourceEvent(string source, double? lagSeconds) => Events.Add((source, lagSeconds));
    }
}

/// <summary>A relist without change enqueues nothing — no SQL pass.</summary>
public class RelistTests
{
    private static System.Text.Json.JsonElement Json(MsSqlOperator.Entities.V1Alpha1.SqlInstance i)
        => JsonDocument.Parse(k8s.KubernetesJson.Serialize(i)).RootElement.Clone();

    private static MsSqlOperator.Entities.V1Alpha1.SqlInstance Instance(long generation, string? annotation = null) => new()
    {
        ApiVersion = "mssql-operator.evariops.com/v1alpha1",
        Kind = "SqlInstance",
        Metadata = new k8s.Models.V1ObjectMeta
        {
            Name = "sql",
            NamespaceProperty = "db",
            Generation = generation,
            Annotations = annotation is null ? null : new Dictionary<string, string> { ["a"] = annotation },
        },
        Spec = new SqlInstanceSpec(),
    };

    [Fact]
    public async Task Relist_EnqueuesOnlyWhatIsNewOrChangedInItsIntent()
    {
        using var runtime = new EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>(null!,
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
            NullLogger<EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>>.Instance);
        var seed = typeof(EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>)
            .GetMethod("SeedFromRelist", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var queue = (WorkQueue)typeof(EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>)
            .GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runtime)!;

        seed.Invoke(runtime, [new List<System.Text.Json.JsonElement> { Json(Instance(1)) }]);
        Assert.Equal(1, queue.Depth); // new
        await queue.GetAsync(CancellationToken.None);
        queue.Done("db/sql", resetBackoff: true);

        seed.Invoke(runtime, [new List<System.Text.Json.JsonElement> { Json(Instance(1)) }]);
        Assert.Equal(0, queue.Depth); // unchanged: no pass

        seed.Invoke(runtime, [new List<System.Text.Json.JsonElement> { Json(Instance(1, "approve")) }]);
        Assert.Equal(1, queue.Depth); // an annotation changed: a pass
    }
}
