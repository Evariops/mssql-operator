using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MsSqlOperator.Services.Breakout;

public interface IBlacklistManager
{
    Task<IReadOnlyDictionary<string, DateTime>> GetBlacklistedNodesAsync(CancellationToken ct);
    Task AddNodeAsync(string nodeName, CancellationToken ct);
    Task RemoveNodeAsync(string nodeName, CancellationToken ct);
    Task<bool> IsBlacklistedAsync(string nodeName, CancellationToken ct);
}

public class BlacklistManager(
    ILogger<BlacklistManager> logger,
    IKubernetesClient kubernetesClient,
    IOptions<BreakoutOptions> options) : IBlacklistManager
{
    private const string DataKey = "blacklisted-nodes";

    public async Task<IReadOnlyDictionary<string, DateTime>> GetBlacklistedNodesAsync(CancellationToken ct)
    {
        var cm = await GetOrCreateConfigMapAsync(ct);
        return ParseBlacklist(cm);
    }

    public async Task AddNodeAsync(string nodeName, CancellationToken ct)
    {
        var cm = await GetOrCreateConfigMapAsync(ct);
        var nodes = ParseBlacklist(cm);

        if (nodes.ContainsKey(nodeName))
            return;

        var mutable = new Dictionary<string, DateTime>(nodes)
        {
            [nodeName] = DateTime.UtcNow
        };

        await UpdateConfigMapAsync(cm, mutable, ct);
        logger.LogWarning("Node {Node} added to blacklist", nodeName);
    }

    public async Task RemoveNodeAsync(string nodeName, CancellationToken ct)
    {
        var cm = await GetOrCreateConfigMapAsync(ct);
        var nodes = ParseBlacklist(cm);

        if (!nodes.ContainsKey(nodeName))
            return;

        var mutable = new Dictionary<string, DateTime>(nodes);
        mutable.Remove(nodeName);

        await UpdateConfigMapAsync(cm, mutable, ct);
        logger.LogInformation("Node {Node} removed from blacklist", nodeName);
    }

    public async Task<bool> IsBlacklistedAsync(string nodeName, CancellationToken ct)
    {
        var nodes = await GetBlacklistedNodesAsync(ct);
        return nodes.ContainsKey(nodeName);
    }

    private async Task<V1ConfigMap> GetOrCreateConfigMapAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var existing = await kubernetesClient.GetAsync<V1ConfigMap>(opts.BlacklistConfigMapName, opts.BlacklistConfigMapNamespace, ct);

        if (existing != null)
            return existing;

        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = opts.BlacklistConfigMapName,
                NamespaceProperty = opts.BlacklistConfigMapNamespace
            },
            Data = new Dictionary<string, string> { [DataKey] = "" }
        };

        return await kubernetesClient.CreateAsync(cm, ct);
    }

    private async Task UpdateConfigMapAsync(V1ConfigMap cm, Dictionary<string, DateTime> nodes, CancellationToken ct)
    {
        var lines = nodes.Select(kv => $"{kv.Key}: \"{kv.Value:O}\"");
        cm.Data ??= new Dictionary<string, string>();
        cm.Data[DataKey] = string.Join("\n", lines);
        await kubernetesClient.UpdateAsync(cm, ct);
    }

    private static Dictionary<string, DateTime> ParseBlacklist(V1ConfigMap cm)
    {
        var result = new Dictionary<string, DateTime>();
        if (cm.Data == null || !cm.Data.TryGetValue(DataKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var node = line[..colonIdx].Trim();
            var dateStr = line[(colonIdx + 1)..].Trim().Trim('"');

            if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                result[node] = dt;
        }

        return result;
    }
}
