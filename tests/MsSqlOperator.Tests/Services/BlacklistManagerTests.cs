using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MsSqlOperator.Services.Breakout;

namespace MsSqlOperator.Tests.Services;

public class BlacklistManagerTests
{
    private readonly Mock<ILogger<BlacklistManager>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly BreakoutOptions _options = new()
    {
        BlacklistConfigMapName = "mssql-failover-blacklist",
        BlacklistConfigMapNamespace = "default"
    };
    private readonly BlacklistManager _manager;

    public BlacklistManagerTests()
    {
        _manager = new BlacklistManager(_logger.Object, _k8sClient.Object, Options.Create(_options));
    }

    [Fact]
    public async Task GetBlacklistedNodes_EmptyConfigMap_ReturnsEmpty()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string> { ["blacklisted-nodes"] = "" }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        var result = await _manager.GetBlacklistedNodesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBlacklistedNodes_WithEntries_ParsesCorrectly()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string>
            {
                ["blacklisted-nodes"] = "node-3: \"2026-05-10T14:30:00.0000000Z\"\nnode-7: \"2026-05-10T14:32:00.0000000Z\""
            }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        var result = await _manager.GetBlacklistedNodesAsync(CancellationToken.None);

        Assert.Equal(2, result.Count());
        Assert.Contains("node-3", result);
        Assert.Contains("node-7", result);
    }

    [Fact]
    public async Task AddNode_CreatesConfigMapIfNotExists()
    {
        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((V1ConfigMap?)null);

        var createdCm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string> { ["blacklisted-nodes"] = "" }
        };

        _k8sClient.Setup(x => x.CreateAsync(It.IsAny<V1ConfigMap>(), It.IsAny<CancellationToken>())).ReturnsAsync(createdCm);

        await _manager.AddNodeAsync("node-1", CancellationToken.None);

        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1ConfigMap>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(x => x.UpdateAsync(It.IsAny<V1ConfigMap>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddNode_AlreadyBlacklisted_NoOp()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string>
            {
                ["blacklisted-nodes"] = "node-1: \"2026-05-10T14:30:00.0000000Z\""
            }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        await _manager.AddNodeAsync("node-1", CancellationToken.None);

        _k8sClient.Verify(x => x.UpdateAsync(It.IsAny<V1ConfigMap>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveNode_ExistingNode_RemovesEntry()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string>
            {
                ["blacklisted-nodes"] = "node-1: \"2026-05-10T14:30:00.0000000Z\"\nnode-2: \"2026-05-10T14:32:00.0000000Z\""
            }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        await _manager.RemoveNodeAsync("node-1", CancellationToken.None);

        _k8sClient.Verify(x => x.UpdateAsync(It.Is<V1ConfigMap>(c =>
            c.Data!["blacklisted-nodes"].Contains("node-2") && !c.Data["blacklisted-nodes"].Contains("node-1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveNode_NotBlacklisted_NoOp()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string> { ["blacklisted-nodes"] = "" }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        await _manager.RemoveNodeAsync("node-1", CancellationToken.None);

        _k8sClient.Verify(x => x.UpdateAsync(It.IsAny<V1ConfigMap>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsBlacklisted_NodeInList_ReturnsTrue()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string>
            {
                ["blacklisted-nodes"] = "node-1: \"2026-05-10T14:30:00.0000000Z\""
            }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        var result = await _manager.IsBlacklistedAsync("node-1", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsBlacklisted_NodeNotInList_ReturnsFalse()
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "mssql-failover-blacklist", NamespaceProperty = "default" },
            Data = new Dictionary<string, string> { ["blacklisted-nodes"] = "" }
        };

        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>("mssql-failover-blacklist", "default", It.IsAny<CancellationToken>())).ReturnsAsync(cm);

        var result = await _manager.IsBlacklistedAsync("node-1", CancellationToken.None);

        Assert.False(result);
    }
}
