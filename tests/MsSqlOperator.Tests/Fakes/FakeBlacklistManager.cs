using MsSqlOperator.Services.Breakout;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IBlacklistManager"/>: records add/remove and serves a
/// configurable blacklist.</summary>
public sealed class FakeBlacklistManager : IBlacklistManager
{
    public readonly List<string> AddNodeCalls = [];
    public readonly List<string> RemoveNodeCalls = [];
    public int GetBlacklistedNodesCallCount { get; private set; }

    public Dictionary<string, DateTime> BlacklistedNodes { get; set; } = [];
    public Func<string, bool> IsBlacklisted { get; set; } = _ => false;

    public Task<IReadOnlyDictionary<string, DateTime>> GetBlacklistedNodesAsync(CancellationToken ct)
    {
        GetBlacklistedNodesCallCount++;
        return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(BlacklistedNodes);
    }

    public Task AddNodeAsync(string nodeName, CancellationToken ct)
    {
        AddNodeCalls.Add(nodeName);
        return Task.CompletedTask;
    }

    public Task RemoveNodeAsync(string nodeName, CancellationToken ct)
    {
        RemoveNodeCalls.Add(nodeName);
        return Task.CompletedTask;
    }

    public Task<bool> IsBlacklistedAsync(string nodeName, CancellationToken ct) => Task.FromResult(IsBlacklisted(nodeName));
}
