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
}
