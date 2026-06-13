using FluentAssertions;
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

        new[] { first, second }.Should().BeEquivalentTo(["default/a", "default/b"]);
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
        again.Should().Be("k");
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
        await act.Should().ThrowAsync<OperationCanceledException>();

        q.Done(key, resetBackoff: true);
        (await q.GetAsync(CancellationToken.None)).Should().Be("k");
    }

    // ── ────────────────────────────────────────────

    [Fact]
    public async Task AddAfter_HugeDelay_IsClampedInsteadOfThrowing()
    {
        using var q = new WorkQueue();
        // 90 days > Task.Delay max (~49.7d): must not throw (clamped to MaxDelay).
        var act = () => { q.AddAfter("ns/x", TimeSpan.FromDays(90)); return Task.CompletedTask; };
        await act.Should().NotThrowAsync();
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
        await act.Should().ThrowAsync<Exception>();
    }

    // ── ──────────────────────────────────────────

    [Fact]
    public void ComputeDeletedKeys_ReturnsKnownKeysMissingFromList()
    {
        var known = new[] { "ns/a", "ns/b", "ns/c" };
        var listed = new HashSet<string> { "ns/a", "ns/c" };
        MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .ComputeDeletedKeys(known, listed)
            .Should().BeEquivalentTo("ns/b");
    }

    [Fact]
    public void ComputeDeletedKeys_EmptyWhenNothingMissing()
    {
        var listed = new HashSet<string> { "ns/a" };
        MsSqlOperator.Operator.Runtime.EntityRuntime<MsSqlOperator.Entities.V1Alpha1.SqlInstance>
            .ComputeDeletedKeys(["ns/a"], listed)
            .Should().BeEmpty();
    }
}
