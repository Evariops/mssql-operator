using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class BlacklistManagerTests
{
    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly BreakoutOptions _options = new()
    {
        BlacklistConfigMapName = "mssql-failover-blacklist",
        BlacklistConfigMapNamespace = "default"
    };
    private readonly BlacklistManager _manager;

    public BlacklistManagerTests()
    {
        _manager = new BlacklistManager(NullLogger<BlacklistManager>.Instance, _k8sClient, Options.Create(_options));
    }

    private static V1ConfigMap ConfigMap(string nodes) => new()
    {
        Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
        Data = new Dictionary<string, string> { ["blacklisted-nodes"] = nodes }
    };

    [Fact]
    public async Task GetBlacklistedNodes_EmptyConfigMap_ReturnsEmpty()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default", ConfigMap(""));

        var result = await _manager.GetBlacklistedNodesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBlacklistedNodes_WithEntries_ParsesCorrectly()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default",
            ConfigMap("node-3: \"2026-05-10T14:30:00.0000000Z\"\nnode-7: \"2026-05-10T14:32:00.0000000Z\""));

        var result = await _manager.GetBlacklistedNodesAsync(CancellationToken.None);

        Assert.Equal(2, result.Count());
        Assert.Contains("node-3", result);
        Assert.Contains("node-7", result);
    }

    [Fact]
    public async Task AddNode_CreatesConfigMapIfNotExists()
    {
        _k8sClient.SetGet<V1ConfigMap>("mssql-failover-blacklist", "default", null);
        _k8sClient.SetCreateReturn(ConfigMap(""));

        await _manager.AddNodeAsync("node-1", CancellationToken.None);

        Assert.Single(_k8sClient.Created<V1ConfigMap>());
        Assert.Single(_k8sClient.Updated<V1ConfigMap>());
    }

    [Fact]
    public async Task AddNode_AlreadyBlacklisted_NoOp()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default",
            ConfigMap("node-1: \"2026-05-10T14:30:00.0000000Z\""));

        await _manager.AddNodeAsync("node-1", CancellationToken.None);

        Assert.Empty(_k8sClient.Updated<V1ConfigMap>());
    }

    [Fact]
    public async Task RemoveNode_ExistingNode_RemovesEntry()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default",
            ConfigMap("node-1: \"2026-05-10T14:30:00.0000000Z\"\nnode-2: \"2026-05-10T14:32:00.0000000Z\""));

        await _manager.RemoveNodeAsync("node-1", CancellationToken.None);

        var updated = Assert.Single(_k8sClient.Updated<V1ConfigMap>());
        Assert.Contains("node-2", updated.Data!["blacklisted-nodes"]);
        Assert.DoesNotContain("node-1", updated.Data["blacklisted-nodes"]);
    }

    [Fact]
    public async Task RemoveNode_NotBlacklisted_NoOp()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default", ConfigMap(""));

        await _manager.RemoveNodeAsync("node-1", CancellationToken.None);

        Assert.Empty(_k8sClient.Updated<V1ConfigMap>());
    }

    [Fact]
    public async Task IsBlacklisted_NodeInList_ReturnsTrue()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default",
            ConfigMap("node-1: \"2026-05-10T14:30:00.0000000Z\""));

        Assert.True(await _manager.IsBlacklistedAsync("node-1", CancellationToken.None));
    }

    [Fact]
    public async Task IsBlacklisted_NodeNotInList_ReturnsFalse()
    {
        _k8sClient.SetGet("mssql-failover-blacklist", "default", ConfigMap(""));

        Assert.False(await _manager.IsBlacklistedAsync("node-1", CancellationToken.None));
    }
}
