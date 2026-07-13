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
    List<string>? DetectedPrimaries = null,
    Dictionary<string, string>? ReplicaVersionsByPod = null
)
{
    /// <summary>Engine build per reachable replica: failover target selection must
    /// never promote an older major version than the last known primary.</summary>
    public Dictionary<string, string> ReplicaVersions => ReplicaVersionsByPod ?? [];
}

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
        List<AgDatabaseStateInfo>? DatabaseStates,
        string? ProductVersion = null);

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
            //
            // A secondary's DMVs only carry AUTHORITATIVE rows about
            // ITSELF — the other replicas come back UNKNOWN through the metadata LEFT JOIN, and its
            // own connected_state describes its session to the (dead) primary. The previous "take
            // the first fallback wholesale" therefore produced a report in which no replica ever
            // looked promotable. Merge instead the LOCAL row of every reachable secondary: each one
            // is the authority on its own role, sync state and per-database queues.
            foreach (var probe in probes.Where(p => p is { Reachable: true, ReplicaStates.Count: > 0 }
                                                    && !string.Equals(p.Role, "PRIMARY", StringComparison.OrdinalIgnoreCase)))
            {
                var local = probe.ReplicaStates!.FirstOrDefault(r =>
                    r.Name.Equals(probe.PodName, StringComparison.OrdinalIgnoreCase));
                if (local is null)
                    continue;
                allReplicaStates.Add(local);
                allDatabaseStates.AddRange((probe.DatabaseStates ?? [])
                    .Where(d => d.ReplicaName.Equals(probe.PodName, StringComparison.OrdinalIgnoreCase)));
            }

            if (allReplicaStates.Count > 0)
                logger.LogWarning("AG {Ag}: no usable primary; merged the local HADR state of {Count} reachable secondary(ies)",
                    name, allReplicaStates.Count);
        }

        // Analyze health
        var disconnectedReplicas = allReplicaStates
            .Where(r => r.ConnectionState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var dc in disconnectedReplicas)
        {
            warnings.Add($"Replica {dc.Name} is DISCONNECTED");
        }

        // The DMV reports 'NOT SYNCHRONIZING' with a SPACE — the old comparison against
        // 'NOT_SYNCHRONIZING' never matched, so a database that had stopped replicating did not make
        // the AG unhealthy and produced no warning. Normalizing underscores covers both spellings.
        var suspendedDbs = allDatabaseStates
            .Where(d => NormalizeState(d.SynchronizationState) is "SUSPENDED" or "NOT SYNCHRONIZING")
            .ToList();

        foreach (var db in suspendedDbs)
        {
            warnings.Add($"Database {db.DatabaseName} on {db.ReplicaName} is {db.SynchronizationState}");
        }

        // A replica the engine itself calls NOT_HEALTHY is a real desync (PARTIALLY_HEALTHY is the
        // normal state while a database seeds, so it only warns).
        var unhealthyReplicas = allReplicaStates
            .Where(r => r.SynchronizationHealth.Equals("NOT_HEALTHY", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var r in unhealthyReplicas)
            warnings.Add($"Replica {r.Name} synchronization health is NOT_HEALTHY");

        var isHealthy = primaryReplica != null
                        && detectedPrimaries.Count == 1
                        && disconnectedReplicas.Count == 0
                        && suspendedDbs.Count == 0
                        && unhealthyReplicas.Count == 0
                        && missingMembership.Count == 0;

        var versions = probes
            .Where(p => p.ProductVersion is { Length: > 0 })
            .ToDictionary(p => p.PodName, p => p.ProductVersion!, StringComparer.OrdinalIgnoreCase);

        return new AgHealthReport(isHealthy, primaryReplica, allReplicaStates, allDatabaseStates,
            warnings, reachableReplicas, missingMembership, detectedPrimaries, versions);
    }

    /// <summary>DMV synchronization_state_desc values use spaces ("NOT SYNCHRONIZING"); some code
    /// paths and tests use underscores. Compare on a single normalized form.</summary>
    internal static string NormalizeState(string state) =>
        state.Replace('_', ' ').Trim().ToUpperInvariant();

    private async Task<ReplicaProbe> ProbeReplicaAsync(
        string name, string ns, string headlessService, int ordinal, string saPassword, CancellationToken ct)
    {
        var podName = $"{name}-{ordinal}";
        var fqdn = $"{podName}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                fqdn, Kubernetes.SqlEndpoints.ContainerPort, saPassword, "master", ct);

            // The engine build of every reachable replica — the failover orchestrator
            // refuses to promote a replica older than the last known primary.
            var version = await agManager.GetProductVersionAsync(conn, ct);

            // Check if this replica knows about the AG (recreated on a blank PV)
            if (!await agManager.AvailabilityGroupExistsAsync(conn, name, ct))
                return new ReplicaProbe(podName, true, $"Replica {podName}: AG '{name}' not found", true, null, null, null, version);

            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);

            if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                // Full state from a primary is authoritative.
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates, version);
            }

            // The HADR DMVs are readable on secondaries too: capture them as fallback so that
            // when no primary answers we still know which secondary is failover-ready.
            try
            {
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates, version);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read fallback HADR state from secondary {Pod}", podName);
                return new ReplicaProbe(podName, true, null, false, role, null, null, version);
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
