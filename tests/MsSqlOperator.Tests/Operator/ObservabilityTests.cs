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
