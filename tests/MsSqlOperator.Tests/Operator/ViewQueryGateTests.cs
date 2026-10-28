using MsSqlOperator.Operator.ViewApi;

namespace MsSqlOperator.Tests.Operator;

/// <summary>The view-API anti-DoS gate (TTL cache + bounded fan-out).</summary>
public class ViewQueryGateTests
{
    private static List<Dictionary<string, object?>> Row(string name) =>
        [new() { ["metadata"] = new Dictionary<string, object?> { ["name"] = name } }];

    [Fact]
    public async Task SecondCallWithinTtl_ServesCacheWithoutRefetch()
    {
        var gate = new ViewQueryGate();
        var fetches = 0;
        Task<List<Dictionary<string, object?>>> Fetch() { fetches++; return Task.FromResult(Row("a")); }

        var first = await gate.GetOrFetchAsync("res", "ns", Fetch, CancellationToken.None);
        var second = await gate.GetOrFetchAsync("res", "ns", Fetch, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first.Items, second!.Items); // same cached list
        Assert.False(second.Stale);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task AfterTtl_Refetches()
    {
        var time = new FakeTime();
        var gate = new ViewQueryGate(time);
        var fetches = 0;
        Task<List<Dictionary<string, object?>>> Fetch() { fetches++; return Task.FromResult(Row("a")); }

        await gate.GetOrFetchAsync("res", "ns", Fetch, CancellationToken.None);
        time.Advance(ViewQueryGate.CacheTtl + TimeSpan.FromSeconds(1));
        await gate.GetOrFetchAsync("res", "ns", Fetch, CancellationToken.None);

        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task DistinctNamespaces_AreCachedSeparately()
    {
        var gate = new ViewQueryGate();
        var fetches = 0;
        Task<List<Dictionary<string, object?>>> Fetch() { fetches++; return Task.FromResult(Row("a")); }

        await gate.GetOrFetchAsync("res", "ns-a", Fetch, CancellationToken.None);
        await gate.GetOrFetchAsync("res", "ns-b", Fetch, CancellationToken.None);

        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Saturated_ServesTheStaleCache_AndSaysSo()
    {
        // Data past the TTL used to be served as if current.
        var time = new FakeTime();
        var gate = new ViewQueryGate(time, acquireTimeout: TimeSpan.FromMilliseconds(50));
        await gate.GetOrFetchAsync("res", "ns", () => Task.FromResult(Row("a")), CancellationToken.None);
        time.Advance(ViewQueryGate.CacheTtl + TimeSpan.FromSeconds(20));

        var blocker = new TaskCompletionSource<List<Dictionary<string, object?>>>();
        var held = Enumerable.Range(0, ViewQueryGate.MaxConcurrentSqlFanouts)
            .Select(i => gate.GetOrFetchAsync("other", $"ns-{i}", () => blocker.Task, CancellationToken.None))
            .ToList();
        var served = await gate.GetOrFetchAsync("res", "ns", () => Task.FromResult(Row("b")), CancellationToken.None);
        blocker.SetResult(Row("x"));
        await Task.WhenAll(held);

        Assert.True(served!.Stale);
        Assert.True(served.Age >= ViewQueryGate.CacheTtl);
        Assert.Equal("a", ((Dictionary<string, object?>)served.Items[0]["metadata"]!)["name"]);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
