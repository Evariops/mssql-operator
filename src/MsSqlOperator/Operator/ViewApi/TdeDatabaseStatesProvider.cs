using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>tdedatabasestates</c>: live per-database TDE encryption state, computed from
/// <c>sys.dm_database_encryption_keys</c> for every database selected by each SqlTdeCertificate.
/// Replaces the removed <c>SqlTdeCertificate.status...databaseSelection.databases[]</c>.
/// </summary>
public sealed class TdeDatabaseStatesProvider(
    ILogger<TdeDatabaseStatesProvider> logger,
    IKubernetesClient kubernetesClient,
    ITargetResolver targetResolver,
    ISqlConnectionFactory connectionFactory,
    ISqlTdeManager tdeManager) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "tdedatabasestates",
        SingularName = "tdedatabasestate",
        Kind = "TdeDatabaseState",
        ShortNames = ["tdedb"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Certificate", Type = "string" },
        new() { Name = "Target", Type = "string" },
        new() { Name = "Database", Type = "string" },
        new() { Name = "Encrypted", Type = "boolean" },
        new() { Name = "State", Type = "string" },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var certs = await kubernetesClient.ListAsync<SqlTdeCertificate>(@namespace, null, ct);

        foreach (var cert in certs)
        {
            var certNs = cert.Metadata.NamespaceProperty;
            List<ResolvedTarget> targets;
            try { targets = await targetResolver.ResolveAsync(cert.Spec.Target, certNs, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "view: resolve targets for {Cert}", cert.Metadata.Name); continue; }

            foreach (var target in targets)
            {
                try
                {
                    await using var conn = await ViewTargets.OpenAsync(target, connectionFactory, targetResolver, ct);
                    if (conn is null)
                    {
                        logger.LogWarning("view: no reachable primary for {Cert}/{Target}", cert.Metadata.Name, target.Name);
                        continue;
                    }

                    var allDbs = await tdeManager.GetUserDatabaseNamesAsync(conn, ct);
                    var selected = DatabaseSelector.Select(cert.Spec.Databases.Selector, allDbs);
                    var states = await tdeManager.GetDatabaseEncryptionStatesAsync(conn, ct);
                    foreach (var db in selected)
                    {
                        var state = states.GetValueOrDefault(db);
                        var itemName = Sanitize($"{cert.Metadata.Name}.{target.Name}.{db}");
                        items.Add(new Dictionary<string, object?>
                        {
                            ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
                            ["kind"] = "TdeDatabaseState",
                            ["metadata"] = new Dictionary<string, object?> { ["name"] = itemName, ["namespace"] = certNs },
                            ["certificate"] = cert.Metadata.Name,
                            ["target"] = target.Name,
                            ["database"] = db,
                            ["encryptionState"] = state ?? "Unknown",
                            ["encrypted"] = state is "Encrypted",
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "view: tde state for {Cert}/{Target}", cert.Metadata.Name, target.Name);
                }
            }
        }

        return name is null ? items : items.Where(i => ItemName(i) == name).ToList();
    }

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells = [ItemName(item), item.GetValueOrDefault("certificate"), item.GetValueOrDefault("target"),
                 item.GetValueOrDefault("database"), item.GetValueOrDefault("encrypted"), item.GetValueOrDefault("encryptionState")]
    };

    private static string ItemName(Dictionary<string, object?> item) =>
        item["metadata"] is Dictionary<string, object?> m && m.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

    private static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
}
