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
    List<string> ReplicasMissingMembership,
    List<string>? DetectedPrimaries = null
);

public class AgHealthChecker(
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    ILogger<AgHealthChecker> logger) : IAgHealthChecker
{
    /// <summary>Upper bound for one full health pass across all replicas.</summary>
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(15);

    private sealed record ReplicaProbe(
        string PodName,
        bool Reachable,
        string? Warning,
        bool MissingMembership,
        string? Role,
        List<AgReplicaStateInfo>? ReplicaStates,
        List<AgDatabaseStateInfo>? DatabaseStates);

    public async Task<AgHealthReport> CheckHealthAsync(SqlAvailabilityGroup ag, string saPassword, CancellationToken ct = default)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var replicaCount = ag.Spec.Replicas.Count;
        var warnings = new List<string>();

        // Probe every replica in parallel with the short probe profile under one
        // global deadline — the previous sequential loop with the default connection profile
        // took ~minutes per unreachable replica, during the outage itself.
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        passCts.CancelAfter(PassTimeout);

        var probes = await Task.WhenAll(Enumerable.Range(0, replicaCount).Select(i =>
            ProbeReplicaAsync(name, ns, headlessService, i, saPassword, passCts.Token)));

        var reachableReplicas = probes.Where(p => p.Reachable).Select(p => p.PodName).ToList();
        var missingMembership = probes.Where(p => p.MissingMembership).Select(p => p.PodName).ToList();
        warnings.AddRange(probes.Where(p => p.Warning != null).Select(p => p.Warning!));

        // Split-brain is reported honestly — when more than one replica claims
        // PRIMARY, no consumer (labels, failover, status) may act on an arbitrary pick.
        var detectedPrimaries = probes
            .Where(p => string.Equals(p.Role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PodName)
            .ToList();

        string? primaryReplica = null;
        var allReplicaStates = new List<AgReplicaStateInfo>();
        var allDatabaseStates = new List<AgDatabaseStateInfo>();

        if (detectedPrimaries.Count > 1)
        {
            warnings.Add($"SPLIT-BRAIN DETECTED: Multiple primaries ({string.Join(" and ", detectedPrimaries)})");
        }
        else if (detectedPrimaries.Count == 1)
        {
            primaryReplica = detectedPrimaries[0];
            var primaryProbe = probes.First(p => p.PodName == primaryReplica);
            allReplicaStates = primaryProbe.ReplicaStates ?? [];
            allDatabaseStates = primaryProbe.DatabaseStates ?? [];
        }

        if (primaryReplica == null)
        {
            if (detectedPrimaries.Count == 0)
                warnings.Add("No primary replica found — AG is in critical state");

            // Surface a secondary-sourced view so the failover decision has data to work with.
            var fallback = probes.FirstOrDefault(p => p is { Reachable: true, ReplicaStates.Count: > 0 }
                                                      && !string.Equals(p.Role, "PRIMARY", StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
            {
                allReplicaStates = fallback.ReplicaStates!;
                allDatabaseStates = fallback.DatabaseStates ?? [];
                logger.LogWarning("AG {Ag}: no usable primary; using HADR state observed from {Pod}", name, fallback.PodName);
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
                        && detectedPrimaries.Count == 1
                        && disconnectedReplicas.Count == 0
                        && suspendedDbs.Count == 0
                        && missingMembership.Count == 0;

        return new AgHealthReport(isHealthy, primaryReplica, allReplicaStates, allDatabaseStates,
            warnings, reachableReplicas, missingMembership, detectedPrimaries);
    }

    private async Task<ReplicaProbe> ProbeReplicaAsync(
        string name, string ns, string headlessService, int ordinal, string saPassword, CancellationToken ct)
    {
        var podName = $"{name}-{ordinal}";
        var fqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(fqdn, 1433, saPassword, "master", ct);

            // Check if this replica knows about the AG (recreated on a blank PV)
            if (!await agManager.AvailabilityGroupExistsAsync(conn, name, ct))
                return new ReplicaProbe(podName, true, $"Replica {podName}: AG '{name}' not found", true, null, null, null);

            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);

            if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                // Full state from a primary is authoritative.
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates);
            }

            // The HADR DMVs are readable on secondaries too: capture them as fallback so that
            // when no primary answers we still know which secondary is failover-ready.
            try
            {
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read fallback HADR state from secondary {Pod}", podName);
                return new ReplicaProbe(podName, true, null, false, role, null, null);
            }
        }
        catch (SqlException ex)
        {
            return new ReplicaProbe(podName, false, $"Replica {podName}: SQL connection failed - {ex.Message}", false, null, null, null);
        }
        catch (Exception ex)
        {
            return new ReplicaProbe(podName, false, $"Replica {podName}: Connection failed - {ex.Message}", false, null, null, null);
        }
    }
}
