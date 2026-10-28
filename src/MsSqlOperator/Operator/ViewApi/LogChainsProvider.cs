using Microsoft.Extensions.Logging;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>logchains</c> — the backups a restore of each SqlAvailabilityGroupDatabase needs, as
/// every replica's msdb recorded them (msdb is not replicated: a log backup taken on the primary of
/// the day is only known there). The files of the operator's own log backups (logBackup.mode Local)
/// are flagged: they stay in its folder on that replica. One row per backup.
/// </summary>
public sealed class LogChainsProvider(
    ILogger<LogChainsProvider> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager) : IViewResourceProvider
{
    /// <summary>Backups listed per database and replica, newest first.</summary>
    public const int PerReplica = 50;

    public ApiResource Resource { get; } = new()
    {
        Name = "logchains",
        SingularName = "logchain",
        Kind = "LogChain",
        ShortNames = ["logchain"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Database", Type = "string" },
        new() { Name = "Replica", Type = "string" },
        new() { Name = "Type", Type = "string" },
        new() { Name = "Finished", Type = "string" },
        new() { Name = "ByOperator", Type = "boolean" },
        new() { Name = "File", Type = "string", Priority = 1 },
        new() { Name = "FirstLSN", Type = "string", Priority = 1 },
        new() { Name = "LastLSN", Type = "string", Priority = 1 },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var agds = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(@namespace, null, ct);
        foreach (var group in agds.GroupBy(d => (d.Metadata.NamespaceProperty, d.Spec.AvailabilityGroupRef.Name)))
        {
            var (ns, agName) = group.Key;
            try
            {
                var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
                if (ag is null)
                    continue;
                if (await SecretValues.TryGetAsync(kubernetesClient, ag.Spec.SaPasswordSecretRef, ns, ct) is not { } saPassword)
                {
                    logger.LogWarning("view: SA password not resolvable for AG {Ag}", agName);
                    continue;
                }

                var target = new ResolvedTarget(TargetKinds.AvailabilityGroup, agName, ns, string.Empty, SqlEndpoints.ContainerPort,
                    saPassword, ag.Spec.Replicas.Count);
                var unreachable = await TargetConnections.ForEveryServerAsync(target, connectionFactory, kubernetesClient, async (conn, replica) =>
                {
                    foreach (var agd in group)
                        foreach (var entry in await agManager.GetBackupChainAsync(conn, agd.Spec.DatabaseName, PerReplica, ct))
                            items.Add(Item(agd, replica ?? agName, entry));
                }, ct);
                foreach (var (replica, error) in unreachable)
                    logger.LogWarning("view: backup chain of AG {Ag} not read on {Replica}: {Error}", agName, replica, error);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "view: backup chains for AG {Ag}", agName);
            }
        }

        return name is null ? items : items.Where(i => ViewItems.Name(i) == name).ToList();
    }

    private static Dictionary<string, object?> Item(SqlAvailabilityGroupDatabase agd, string replica, BackupChainEntry entry) => new()
    {
        ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
        ["kind"] = "LogChain",
        ["metadata"] = new Dictionary<string, object?>
        {
            ["name"] = ViewItems.Sanitize($"{agd.Metadata.Name}.{replica}.{entry.LastLsn}"),
            ["namespace"] = agd.Metadata.NamespaceProperty,
        },
        ["database"] = agd.Spec.DatabaseName,
        ["replica"] = replica,
        ["type"] = entry.Type switch { "D" => "Full", "I" => "Differential", _ => "Log" },
        ["finishedAt"] = entry.FinishedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["byOperator"] = entry.File.StartsWith(SqlAvailabilityGroupDatabaseController.OperatorLogBackupFolder + "/", StringComparison.Ordinal),
        ["file"] = entry.File,
        ["firstLsn"] = entry.FirstLsn,
        ["lastLsn"] = entry.LastLsn,
    };

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells =
        [
            ViewItems.Name(item), item.GetValueOrDefault("database"), item.GetValueOrDefault("replica"),
            item.GetValueOrDefault("type"), item.GetValueOrDefault("finishedAt"), item.GetValueOrDefault("byOperator"),
            item.GetValueOrDefault("file"), item.GetValueOrDefault("firstLsn"), item.GetValueOrDefault("lastLsn"),
        ]
    };
}
