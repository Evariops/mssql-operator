using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>agreplicastates</c>: live Availability Group replica health, computed from
/// <c>sys.dm_hadr_availability_replica_states</c> (and friends) on the live primary of each
/// SqlAvailabilityGroup. Replaces the removed per-replica detail that used to live in
/// <c>SqlAvailabilityGroup.status</c>. One row per replica.
/// </summary>
public sealed class AgReplicaStatesProvider(
    ILogger<AgReplicaStatesProvider> logger,
    IKubernetesClient kubernetesClient,
    ITargetResolver targetResolver,
    ISqlAgManager agManager) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "agreplicastates",
        SingularName = "agreplicastate",
        Kind = "AgReplicaState",
        ShortNames = ["agrep"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Group", Type = "string" },
        new() { Name = "Replica", Type = "string" },
        new() { Name = "Role", Type = "string" },
        new() { Name = "SyncHealth", Type = "string" },
        new() { Name = "Connection", Type = "string" },
        new() { Name = "Operational", Type = "string" },
        new() { Name = "FailoverReady", Type = "boolean" },
        new() { Name = "AvailabilityMode", Type = "string", Priority = 1 },
        new() { Name = "AllowConnections", Type = "string", Priority = 1 },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var ags = await kubernetesClient.ListAsync<SqlAvailabilityGroup>(@namespace, null, ct);

        foreach (var ag in ags)
        {
            var agNs = ag.Metadata.NamespaceProperty;
            var agName = ag.Metadata.Name;

            string? saPassword;
            try
            {
                saPassword = await SecretValues.TryGetAsync(kubernetesClient, ag.Spec.SaPasswordSecretRef, agNs, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One AG's unreadable Secret leaves it out of the list — logged, no longer silent.
                logger.LogWarning(ex, "view: could not read the SA password Secret of AG {Ag}", agName);
                continue;
            }
            if (saPassword is null)
            {
                logger.LogWarning("view: SA password not resolvable for AG {Ag}", agName);
                continue;
            }

            try
            {
                var primary = await targetResolver.FindPrimaryReplicaAsync(agName, agNs, saPassword, ag.Spec.Replicas.Count, ct);
                if (primary is null)
                {
                    logger.LogWarning("view: no reachable primary for AG {Ag}", agName);
                    continue;
                }

                await using var conn = primary.Value.Connection;
                var states = await agManager.GetReplicaStatesAsync(conn, agName, ct);
                foreach (var s in states)
                {
                    items.Add(new Dictionary<string, object?>
                    {
                        ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
                        ["kind"] = "AgReplicaState",
                        ["metadata"] = new Dictionary<string, object?> { ["name"] = ViewItems.Sanitize($"{agName}.{s.Name}"), ["namespace"] = agNs },
                        ["group"] = agName,
                        ["replica"] = s.Name,
                        ["role"] = s.Role,
                        ["syncHealth"] = s.SynchronizationHealth,
                        ["connectionState"] = s.ConnectionState,
                        ["operationalState"] = s.OperationalState,
                        ["failoverReady"] = s.IsFailoverReady,
                        ["availabilityMode"] = s.AvailabilityMode,
                        ["allowConnections"] = s.AllowConnections,
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "view: replica states for AG {Ag}", agName);
            }
        }

        return name is null ? items : items.Where(i => ViewItems.Name(i) == name).ToList();
    }

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells =
        [
            ViewItems.Name(item), item.GetValueOrDefault("group"), item.GetValueOrDefault("replica"),
            item.GetValueOrDefault("role"), item.GetValueOrDefault("syncHealth"),
            item.GetValueOrDefault("connectionState"), item.GetValueOrDefault("operationalState"),
            item.GetValueOrDefault("failoverReady"), item.GetValueOrDefault("availabilityMode"),
            item.GetValueOrDefault("allowConnections"),
        ]
    };

}
