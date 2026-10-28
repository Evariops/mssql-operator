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
    Dictionary<string, string>? ReplicaVersionsByPod = null,
    List<string>? AuthenticationFailedReplicas = null,
    int? RequiredSynchronizedSecondaries = null,
    List<string>? ResolvingReplicaPods = null
)
{
    /// <summary>Replicas whose own role is RESOLVING: an old primary taken OFFLINE
    /// that did not take the secondary role stays there — not writing, not replicating.</summary>
    public List<string> ResolvingReplicas => ResolvingReplicaPods ?? [];

    /// <summary>Engine build per reachable replica: failover target selection must
    /// never promote an older major version than the last known primary.</summary>
    public Dictionary<string, string> ReplicaVersions => ReplicaVersionsByPod ?? [];

    /// <summary>Replicas that answered but refused the operator's login (error 18456) — typically
    /// during an sa password rotation. They are alive: never a reason to fail over.</summary>
    public List<string> AuthFailures => AuthenticationFailedReplicas ?? [];
}

public class AgHealthChecker(
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    ILogger<AgHealthChecker> logger) : IAgHealthChecker
{
    /// <summary>Upper bound for one full health pass across all replicas:
    /// <c>failover.healthCheckTimeoutSeconds</c>, never below 5 seconds.</summary>
    internal static TimeSpan PassTimeout(SqlAvailabilityGroup ag)
        => TimeSpan.FromSeconds(Math.Max(5, (ag.Spec.Failover ?? new AgFailoverSpec()).HealthCheckTimeoutSeconds));

    private sealed record ReplicaProbe(
        string PodName,
        bool Reachable,
        string? Warning,
        bool MissingMembership,
        string? Role,
        List<AgReplicaStateInfo>? ReplicaStates,
        List<AgDatabaseStateInfo>? DatabaseStates,
        string? ProductVersion = null,
        bool AuthFailed = false,
        int? RequiredSynchronizedSecondaries = null);

    /// <summary>SQL Server error 18456: login failed.</summary>
    private const int LoginFailedError = 18456;

    public async Task<AgHealthReport> CheckHealthAsync(SqlAvailabilityGroup ag, string saPassword, CancellationToken ct = default)
    {
        await MsSqlOperator.Operator.Failpoint.HitAsync(MsSqlOperator.Operator.Failpoint.Names.StateReadSlow, ct);
        await MsSqlOperator.Operator.Failpoint.HitAsync(MsSqlOperator.Operator.Failpoint.Names.StateReadFail, ct);
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var replicaCount = ag.Spec.Replicas.Count;
        var warnings = new List<string>();

        // Probe every replica in parallel with the short probe profile under one
        // global deadline — the previous sequential loop with the default connection profile
        // took ~minutes per unreachable replica, during the outage itself.
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        passCts.CancelAfter(PassTimeout(ag));

        var probes = await Task.WhenAll(Enumerable.Range(0, replicaCount).Select(i =>
            BoundedProbeAsync(name, ns, headlessService, i, saPassword, PassTimeout(ag), passCts.Token)));

        var reachableReplicas = probes.Where(p => p.Reachable).Select(p => p.PodName).ToList();
        var authFailures = probes.Where(p => p.AuthFailed).Select(p => p.PodName).ToList();
        var missingMembership = probes.Where(p => p.MissingMembership).Select(p => p.PodName).ToList();
        warnings.AddRange(probes.Where(p => p.Warning != null).Select(p => p.Warning!));

        // Split-brain is reported honestly — when more than one replica claims
        // PRIMARY, no consumer (labels, failover, status) may act on an arbitrary pick.
        var detectedPrimaries = probes
            .Where(p => string.Equals(p.Role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PodName)
            .ToList();

        var resolving = probes
            .Where(p => string.Equals(p.Role, "RESOLVING", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PodName)
            .ToList();

        string? primaryReplica = null;
        int? requiredSync = null;
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
            requiredSync = primaryProbe.RequiredSynchronizedSecondaries;
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
        // normal state while a database seeds, so it only warns). The health of a replica rolls up
        // its databases: in an AG that has none yet, the engine calls every replica NOT_HEALTHY.
        // Counting that made a new AG Degraded, and the SqlAvailabilityGroupDatabase — which waits
        // for Running — never added the first database.
        var unhealthyReplicas = allDatabaseStates.Count == 0
            ? []
            : allReplicaStates
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
            warnings, reachableReplicas, missingMembership, detectedPrimaries, versions, authFailures, requiredSync, resolving);
    }

    /// <summary>DMV synchronization_state_desc values use spaces ("NOT SYNCHRONIZING"); some code
    /// paths and tests use underscores. Compare on a single normalized form.</summary>
    internal static string NormalizeState(string state) =>
        state.Replace('_', ' ').Trim().ToUpperInvariant();

    /// <summary>Grace given to a probe past the pass deadline to honour its cancellation.</summary>
    private static readonly TimeSpan ProbeCancellationGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A replica that holds its TCP sessions open without ever answering (a frozen
    /// process) made a SqlClient call ignore its cancellation, and Task.WhenAll held the whole pass —
    /// 7.5 minutes without detection. Past the deadline the probe is abandoned and the replica counted
    /// unreachable; the abandoned call ends when its socket does.
    /// </summary>
    private async Task<ReplicaProbe> BoundedProbeAsync(
        string name, string ns, string headlessService, int ordinal, string saPassword, TimeSpan deadline, CancellationToken ct)
    {
        var probe = ProbeReplicaAsync(name, ns, headlessService, ordinal, saPassword, ct);
        try
        {
            return await probe.WaitAsync(deadline + ProbeCancellationGrace, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            _ = probe.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var podName = $"{name}-{ordinal}";
            logger.LogWarning("Replica {Pod} did not answer within {Deadline}s, nor honoured the cancellation: counted unreachable",
                podName, (deadline + ProbeCancellationGrace).TotalSeconds);
            return new ReplicaProbe(podName, false, $"Replica {podName} is unreachable", false, null, null, null);
        }
    }

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
                // Full state from a primary is authoritative. The commit durability in force
                // (REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT) is recorded too: it decides whether a
                // later failover without this primary can be lossless.
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                var requiredSync = await agManager.GetRequiredSyncSecondariesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates, version,
                    RequiredSynchronizedSecondaries: requiredSync);
            }

            // The HADR DMVs are readable on secondaries too: capture them as fallback so that
            // when no primary answers we still know which secondary is failover-ready.
            try
            {
                var replicaStates = await agManager.GetReplicaStatesAsync(conn, name, ct);
                var databaseStates = await agManager.GetDatabaseStatesAsync(conn, name, ct);
                return new ReplicaProbe(podName, true, null, false, role, replicaStates, databaseStates, version);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not read fallback HADR state from secondary {Pod}", podName);
                return new ReplicaProbe(podName, true, null, false, role, null, null, version);
            }
        }
        catch (SqlException ex) when (ex.Number == LoginFailedError)
        {
            // The replica answered and refused the login — alive, not unreachable.
            return new ReplicaProbe(podName, false, $"Replica {podName}: login failed (sa password out of sync?)", false, null, null, null,
                AuthFailed: true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The warning feeds the Ready condition — no exception text (timeouts carry measured
            // durations, so the status changed on every pass during exactly the incidents).
            logger.LogDebug(ex, "Replica {Pod} unreachable", podName);
            return new ReplicaProbe(podName, false, $"Replica {podName} is unreachable", false, null, null, null);
        }
    }
}
