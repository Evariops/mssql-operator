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
    List<string> Warnings,
    List<string> ReachableReplicas,
    List<string> ReplicasMissingMembership
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
        // Best-effort state from any reachable secondary, used when no primary answers so the
        // operator can still see per-replica failover-readiness/lag and pick a promotion target.
        var fallbackReplicaStates = new List<AgReplicaStateInfo>();
        var fallbackDatabaseStates = new List<AgDatabaseStateInfo>();
        string? primaryReplica = null;
        var reachableReplicas = new List<string>();
        // Reachable replicas whose local instance has no record of the AG — e.g. recreated on a
        // blank PV — so the operator can attempt to re-join them.
        var missingMembership = new List<string>();

        // Try to connect to each replica and gather state
        for (var i = 0; i < replicaCount; i++)
        {
            var podName = $"{name}-{i}";
            var fqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";

            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(fqdn, 1433, saPassword, "master", ct);

                reachableReplicas.Add(podName);

                // Check if this replica knows about the AG
                if (!await agManager.AvailabilityGroupExistsAsync(conn, name, ct))
                {
                    warnings.Add($"Replica {podName}: AG '{name}' not found");
                    missingMembership.Add(podName);
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

                    // Get full state from primary (authoritative)
                    allReplicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                    allDatabaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                }
                else if (fallbackReplicaStates.Count == 0)
                {
                    // The HADR DMVs are readable on secondaries too. Capture them once so that if
                    // the primary turns out to be unreachable we still know which secondary is
                    // failover-ready instead of flying blind.
                    try
                    {
                        fallbackReplicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                        fallbackDatabaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not read fallback HADR state from secondary {Pod}", podName);
                    }
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
            // Surface the secondary-sourced view so the failover decision has data to work with.
            if (allReplicaStates.Count == 0 && fallbackReplicaStates.Count > 0)
            {
                allReplicaStates = fallbackReplicaStates;
                allDatabaseStates = fallbackDatabaseStates;
                logger.LogWarning("AG {Ag}: no primary reachable; using HADR state observed from a secondary", name);
            }
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
                        && missingMembership.Count == 0
                        && !warnings.Any(w => w.Contains("SPLIT-BRAIN"));

        return new AgHealthReport(isHealthy, primaryReplica, allReplicaStates, allDatabaseStates, warnings, reachableReplicas, missingMembership);
    }
}
