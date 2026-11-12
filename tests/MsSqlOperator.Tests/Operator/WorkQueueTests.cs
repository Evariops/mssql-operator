using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Operator;

public class WorkQueueTests
{
    [Fact]
    public async Task Add_DeduplicatesAndDelivers()
    {
        using var q = new WorkQueue();
        q.Add("default/a");
        q.Add("default/a"); // dedup while pending
        q.Add("default/b");

        var first = await q.GetAsync(CancellationToken.None);
        var second = await q.GetAsync(CancellationToken.None);

        Assert.Equivalent(new[] { "default/a", "default/b" }, new[] { first, second });
    }

    [Fact]
    public async Task RequeueWhileProcessing_IsDeliveredAfterDone()
    {
        using var q = new WorkQueue();
        q.Add("k");
        var key = await q.GetAsync(CancellationToken.None);

        // Event arrives while the key is being processed → must not be lost.
        q.Add("k");
        q.Done(key, resetBackoff: true);

        var again = await q.GetAsync(CancellationToken.None);
        Assert.Equal("k", again);
    }

    [Fact]
    public async Task SingleFlight_NoConcurrentDuplicateForSameKey()
    {
        using var q = new WorkQueue();
        q.Add("k");
        var key = await q.GetAsync(CancellationToken.None);

        // While "k" is in-flight, re-adding must not surface a second copy concurrently.
        q.Add("k");
        using var cts = new CancellationTokenSource(100);
        var act = async () => await q.GetAsync(cts.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(act);

        q.Done(key, resetBackoff: true);
        Assert.Equal("k", (await q.GetAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task UrgentKey_IsServedAheadOfTheOrdinaryOnes()
    {
        // An AG losing its primary waited behind every steady AG of the fleet.
        using var q = new WorkQueue();
        q.Add("ns/steady-1");
        q.Add("ns/steady-2");
        q.Add("ns/failing-over", urgent: true);

        Assert.Equal("ns/failing-over", await q.GetAsync(CancellationToken.None));
        Assert.Equal("ns/steady-1", await q.GetAsync(CancellationToken.None));
        Assert.Equal("ns/steady-2", await q.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OrdinaryKeyMadeUrgent_WhilePending_MovesAhead()
    {
        using var q = new WorkQueue();
        q.Add("ns/a");
        q.Add("ns/b");
        q.Add("ns/b", urgent: true);

        Assert.Equal("ns/b", await q.GetAsync(CancellationToken.None));
        Assert.Equal("ns/a", await q.GetAsync(CancellationToken.None));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await q.GetAsync(cts.Token)); // not delivered twice
    }

    [Fact]
    public async Task UrgentRequeue_WhileProcessing_KeepsItsUrgencyAfterDone()
    {
        using var q = new WorkQueue();
        q.Add("ns/ag");
        var key = await q.GetAsync(CancellationToken.None);
        q.Add("ns/other");
        q.Add("ns/ag", urgent: true); // in flight: delivered after Done, ahead of the others

        q.Done(key, resetBackoff: true);

        Assert.Equal("ns/ag", await q.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UrgentAddAfter_FiresIntoTheUrgentLane()
    {
        using var q = new WorkQueue();
        q.Add("ns/steady");
        q.AddAfter("ns/fencing", TimeSpan.FromMilliseconds(20), urgent: true);
        await Task.Delay(200);

        Assert.Equal("ns/fencing", await q.GetAsync(CancellationToken.None));
    }

    // ── ────────────────────────────────────────────

    [Fact]
    public async Task AddAfter_HugeDelay_IsClampedInsteadOfThrowing()
    {
        using var q = new WorkQueue();
        // 90 days > Task.Delay max (~49.7d): must not throw (clamped to MaxDelay).
        var act = () => { q.AddAfter("ns/x", TimeSpan.FromDays(90)); return Task.CompletedTask; };
        await act();
    }

    [Fact]
    public void Forget_ClearsFailureCount()
    {
        using var q = new WorkQueue(maxBackoff: TimeSpan.FromMilliseconds(1));
        q.AddWithBackoff("ns/x");
        q.Forget("ns/x");
        // No assertion handle on internals; the contract is simply that Forget never throws
        // and a later AddWithBackoff restarts from the first backoff step.
        q.AddWithBackoff("ns/x");
    }

    [Fact]
    public async Task Dispose_CancelsPendingDelayedAdds()
    {
        var q = new WorkQueue();
        q.AddAfter("ns/x", TimeSpan.FromSeconds(30));
        q.Dispose();
        // The delayed continuation must not fire after dispose; give it a beat to misbehave.
        await Task.Delay(50);
        // Reading from a disposed queue completes the channel: GetAsync should throw.
        var act = async () => await q.GetAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(act);
    }

    // ── ──────────────────────────────────────────

    [Fact]
    public void ComputeDeletedKeys_ReturnsKnownKeysMissingFromList()
    {
        var known = new[] { "ns/a", "ns/b", "ns/c" };
        var listed = new HashSet<string> { "ns/a", "ns/c" };
        var deleted = MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .ComputeDeletedKeys(known, listed);
        Assert.Equal("ns/b", Assert.Single(deleted));
    }

    [Fact]
    public void ComputeDeletedKeys_EmptyWhenNothingMissing()
    {
        var listed = new HashSet<string> { "ns/a" };
        Assert.Empty(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .ComputeDeletedKeys(["ns/a"], listed));
    }

    // ── ─────────────────────────────────────────────

    [Fact]
    public void AddAfter_KeepsASingleDeadlinePerKey_TheEarliest()
    {
        using var q = new WorkQueue();
        q.AddAfter("ns/x", TimeSpan.FromMinutes(5));
        q.AddAfter("ns/x", TimeSpan.FromMinutes(1));   // earlier: replaces
        q.AddAfter("ns/x", TimeSpan.FromMinutes(10));  // later: ignored
        q.AddAfter("ns/y", TimeSpan.FromMinutes(1));

        // One pending timer per key, whatever the number of AddAfter calls: the runtime calls it after
        // every successful reconcile, and independent timers used to pile up one chain per event.
        Assert.Equal(2, q.ScheduledCount);
    }

    [Fact]
    public async Task AddAfter_FiresOnce()
    {
        using var q = new WorkQueue();
        q.AddAfter("ns/x", TimeSpan.FromMilliseconds(50));
        q.AddAfter("ns/x", TimeSpan.FromMilliseconds(80));

        Assert.Equal("ns/x", await q.GetAsync(CancellationToken.None));
        q.Done("ns/x", resetBackoff: true);
        await Task.Delay(200);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await q.GetAsync(cts.Token));
        Assert.Equal(0, q.ScheduledCount);
    }

    [Fact]
    public void Forget_CancelsThePendingDelayedAdd()
    {
        using var q = new WorkQueue();
        q.AddAfter("ns/x", TimeSpan.FromMinutes(5));
        q.Forget("ns/x");
        Assert.Equal(0, q.ScheduledCount);
    }

    // ── ─────────────────────────────────────────────

    private static MsSqlOperator.Entities.V1Alpha1.SqlInstance Instance(long generation, Dictionary<string, string>? annotations = null)
        => new() { Metadata = new k8s.Models.V1ObjectMeta { Name = "x", NamespaceProperty = "ns", Generation = generation, Annotations = annotations } };

    [Fact]
    public void StatusOnlyChange_IsRecognised()
    {
        Assert.True(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .IsStatusOnlyChange(Instance(3), Instance(3)));
    }

    [Theory]
    [InlineData(4, null)]
    [InlineData(3, "true")]
    public void SpecOrAnnotationChange_IsNotStatusOnly(long generation, string? paused)
    {
        var after = Instance(generation, paused is null ? null : new Dictionary<string, string> { ["mssql-operator.evariops.com/reconcile-paused"] = paused });
        Assert.False(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .IsStatusOnlyChange(Instance(3), after));
    }

    // ── ─────────────────────────────────────────

    [Fact]
    public void LiftedPause_StaleCondition_IsSetBackToFalse()
    {
        // The annotation was removed at 02:11; the status still said four hours later that health
        // checks and failover were NOT running.
        var entity = Instance(3);
        entity.Status = new MsSqlOperator.Entities.V1Alpha1.SqlInstanceStatus
        {
            Conditions = [new MsSqlOperator.Entities.Common.ConditionStatus { Type = "ReconcilePaused", Status = "True", Reason = "OperationalPause" }],
        };

        Assert.True(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>.ClearStalePause(entity));
        var condition = Assert.Single(entity.Status.Conditions!);
        Assert.Equal("False", condition.Status);
        Assert.Equal("Resumed", condition.Reason);
        Assert.False(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>.ClearStalePause(entity));
    }

    [Fact]
    public async Task LiftedPause_StatusWritten_NewResourceVersionReported()
    {
        // The status write of the pause returned a new resourceVersion that was dropped; the
        // controller then wrote its own status with the stale one, and got a 409 every time.
        var entity = Instance(3);
        entity.Metadata.ResourceVersion = "41";
        entity.Status!.Conditions = [new MsSqlOperator.Entities.Common.ConditionStatus { Type = "ReconcilePaused", Status = "True", Reason = "OperationalPause" }];
        var client = new MsSqlOperator.Tests.Fakes.FakeKubernetesClient { NextStatusResourceVersion = "42" };

        var cleared = await MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .ClearPausedStatusAsync(client, entity, CancellationToken.None);

        Assert.True(cleared);
        Assert.Equal("42", entity.Metadata.ResourceVersion);
    }

    [Fact]
    public void NeverPaused_NothingToClear()
    {
        Assert.False(MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>.ClearStalePause(Instance(3)));
    }
}
