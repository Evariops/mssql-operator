using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Health;

public interface IAgHealthChecker
{
    /// <summary>Check health of all replicas and return status info.</summary>
    Task<AgHealthReport> CheckHealthAsync(SqlAvailabilityGroup ag, string saPassword, CancellationToken ct = default);
}

public record AgHealthReport(
    bool IsHealthy,
    string? PrimaryReplica,
    List<AgReplicaStateInfo> ReplicaStates,
    List<AgDatabaseStateInfo> DatabaseStates,
    List<string> Warnings
);

public class AgHealthChecker(
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    ILogger<AgHealthChecker> logger) : IAgHealthChecker
{
    public async Task<AgHealthReport> CheckHealthAsync(SqlAvailabilityGroup ag, string saPassword, CancellationToken ct = default)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var replicaCount = ag.Spec.Replicas.Count;
        var warnings = new List<string>();
        var allReplicaStates = new List<AgReplicaStateInfo>();
        var allDatabaseStates = new List<AgDatabaseStateInfo>();
        string? primaryReplica = null;

        // Try to connect to each replica and gather state
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var fqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";

            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(fqdn, 1433, saPassword, "master", ct);

                // Check if this replica knows about the AG
                if (!await agManager.AvailabilityGroupExistsAsync(conn, name, ct))
                {
                    warnings.Add($"Replica {podName}: AG '{name}' not found");
                    continue;
                }

                // Get replica role
                var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
                if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                {
                    if (primaryReplica != null)
                    {
                        warnings.Add($"SPLIT-BRAIN DETECTED: Multiple primaries ({primaryReplica} and {podName})");
                    }
                    primaryReplica = podName;

                    // Get full state from primary
                    allReplicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                    allDatabaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                }
            }
            catch (SqlException ex)
            {
                warnings.Add($"Replica {podName}: SQL connection failed - {ex.Message}");
            }
            catch (Exception ex)
            {
                warnings.Add($"Replica {podName}: Connection failed - {ex.Message}");
            }
        }

        if (primaryReplica == null)
        {
            warnings.Add("No primary replica found — AG is in critical state");
        }

        // Analyze health
        var disconnectedReplicas = allReplicaStates
            .Where(r => r.ConnectionState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var dc in disconnectedReplicas)
        {
            warnings.Add($"Replica {dc.Name} is DISCONNECTED");
        }

        var suspendedDbs = allDatabaseStates
            .Where(d => d.SynchronizationState.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase) ||
                        d.SynchronizationState.Equals("NOT_SYNCHRONIZING", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var db in suspendedDbs)
        {
            warnings.Add($"Database {db.DatabaseName} is {db.SynchronizationState}");
        }

        var isHealthy = primaryReplica != null
                        && disconnectedReplicas.Count == 0
                        && suspendedDbs.Count == 0
                        && !warnings.Any(w => w.Contains("SPLIT-BRAIN"));

        return new AgHealthReport(isHealthy, primaryReplica, allReplicaStates, allDatabaseStates, warnings);
    }
}
