using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>storedproceduredeployments</c>: live per-procedure deployment state, computed from
/// <c>sys.procedures</c> for every database matched by each SqlStoredProcedure. Replaces the removed
/// <c>SqlStoredProcedure.status...deployedProcedures[]</c> tree. Only inline procedures (whose names
/// are known without a GitHub fetch) are reported.
/// </summary>
public sealed class StoredProcedureDeploymentsProvider(
    ILogger<StoredProcedureDeploymentsProvider> logger,
    IKubernetesClient kubernetesClient,
    ITargetResolver targetResolver,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    ISqlStoredProcedureManager storedProcManager) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "storedproceduredeployments",
        SingularName = "storedproceduredeployment",
        Kind = "StoredProcedureDeployment",
        ShortNames = ["spdeploy"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Resource", Type = "string" },
        new() { Name = "Target", Type = "string" },
        new() { Name = "Database", Type = "string" },
        new() { Name = "Procedure", Type = "string" },
        new() { Name = "Deployed", Type = "boolean" },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var resources = await kubernetesClient.ListAsync<SqlStoredProcedure>(@namespace, null, ct);

        foreach (var sp in resources)
        {
            var spNs = sp.Metadata.NamespaceProperty;
            var procs = (sp.Spec.Inline ?? [])
                .Select(p => (Schema: string.IsNullOrWhiteSpace(p.Schema) ? "dbo" : p.Schema, p.Name))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
            if (procs.Count == 0) continue;

            List<ResolvedTarget> targets;
            try { targets = await targetResolver.ResolveAsync(sp.Spec.Target, spNs, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "view: resolve targets for {Sp}", sp.Metadata.Name); continue; }

            foreach (var target in targets)
            {
                try
                {
                    await using var conn = await ViewTargets.OpenAsync(target, connectionFactory, targetResolver, ct);
                    if (conn is null)
                    {
                        logger.LogWarning("view: no reachable primary for {Sp}/{Target}", sp.Metadata.Name, target.Name);
                        continue;
                    }

                    var allDbs = await sqlServerManager.GetDatabaseNamesAsync(conn, ct);
                    var databases = databaseFilter.Apply(allDbs, sp.Spec.Databases, sp.Spec.DatabaseFilter);
                    var existing = await storedProcManager.GetProceduresAsync(conn, databases, ct);
                    foreach (var db in databases)
                        foreach (var (schema, procName) in procs)
                        {
                            var exists = existing.Contains((db, schema, procName));
                            var itemName = ViewItems.Sanitize($"{sp.Metadata.Name}.{target.Name}.{db}.{schema}.{procName}");
                            items.Add(new Dictionary<string, object?>
                            {
                                ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
                                ["kind"] = "StoredProcedureDeployment",
                                ["metadata"] = new Dictionary<string, object?> { ["name"] = itemName, ["namespace"] = spNs },
                                ["resource"] = sp.Metadata.Name,
                                ["target"] = target.Name,
                                ["database"] = db,
                                ["procedure"] = $"{schema}.{procName}",
                                ["deployed"] = exists,
                            });
                        }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "view: stored-proc state for {Sp}/{Target}", sp.Metadata.Name, target.Name);
                }
            }
        }

        return name is null ? items : items.Where(i => ViewItems.Name(i) == name).ToList();
    }

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells = [ViewItems.Name(item), item.GetValueOrDefault("resource"), item.GetValueOrDefault("target"),
                 item.GetValueOrDefault("database"), item.GetValueOrDefault("procedure"), item.GetValueOrDefault("deployed")]
    };
}
