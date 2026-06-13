using FluentAssertions;
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

        first.Should().NotBeNull();
        second.Should().BeSameAs(first); // same cached list
        fetches.Should().Be(1);
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

        fetches.Should().Be(2);
    }

    [Fact]
    public async Task DistinctNamespaces_AreCachedSeparately()
    {
        var gate = new ViewQueryGate();
        var fetches = 0;
        Task<List<Dictionary<string, object?>>> Fetch() { fetches++; return Task.FromResult(Row("a")); }

        await gate.GetOrFetchAsync("res", "ns-a", Fetch, CancellationToken.None);
        await gate.GetOrFetchAsync("res", "ns-b", Fetch, CancellationToken.None);

        fetches.Should().Be(2);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
