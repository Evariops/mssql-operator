using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Election;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Core.Timing;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// The operator side of the quorum elections, at safety level 2. It keeps the
/// watchdog job of every replica provisioned (Shadow: it decides and records; Enforce: it steps a
/// primary without quorum down), reads their heartbeats (<c>WatchdogUnavailable</c>), and — in
/// Shadow — runs the election core on the same observation as the current path and logs both
/// decisions: every gap is to be explained before level 2 goes to Enforce. The commands of
/// the core are not executed by this shell yet; level 2 in Enforce waits for lab validation.
/// </summary>
public sealed class ElectionShell(ISqlConnectionFactory connectionFactory, ISqlAgentManager agentManager,
    Operator.Registration.OperatorOptions options, Metrics.OperatorMetrics? metrics, ILogger<ElectionShell> logger,
    IOperatorEpoch? operatorEpoch = null)
{
    public const string WatchdogUnavailableCondition = "WatchdogUnavailable";

    /// <summary>The watchdog jobs were provisioned on the replicas — what says, across operator
    /// restarts, that they are to be turned off once level 2 is.</summary>
    public const string WatchdogProvisionedCondition = "QuorumWatchdogProvisioned";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _provisioned = new();

    /// <summary>Per AG uid and replica: whether its watchdog ran at the last read. An unreachable
    /// replica was taken as without a watchdog, so the core could never conclude that an unreachable
    /// old primary had stepped down — it never decided an election.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _aliveAtLastRead = new();

    public TimingBudget Budget { get; } = TimingBudget.Default;

    public SafetyMode? Mode(SqlAvailabilityGroup ag)
        => SafetyLevel.Mode(2, SafetyLevel.Effective(ag.Metadata.Annotations?.TryGetValue(SafetyLevel.Annotation, out var l) == true ? l : null,
            options.SafetyLevelMax), options.SafetyLevelMax);

    /// <summary>Provisions the watchdogs (when their definition changed) and reads them, then runs the
    /// core in Shadow. Returns the watchdog state of each reachable replica.</summary>
    public async Task<Dictionary<string, WatchdogState?>> RunAsync(SqlAvailabilityGroup ag, Health.AgHealthReport health, string saPassword,
        string headlessService, string currentOutcome, DateTimeOffset now, CancellationToken ct)
    {
        var states = new Dictionary<string, WatchdogState?>();
        var mode = Mode(ag);
        if (mode is null)
        {
            await TurnOffWatchdogsAsync(ag, health, saPassword, headlessService, ct);
            return states;
        }
        ConditionOps.Set(ag.Status!.Conditions ??= [], WatchdogProvisionedCondition, "True", mode.Value.ToString(),
            $"The quorum watchdog job runs on every replica ({mode.Value}).");

        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var marker = QuorumWatchdog.Marker(Budget, mode == SafetyMode.Enforce);
        foreach (var replica in health.ReachableReplicas)
        {
            try
            {
                await using var conn = await connectionFactory.OpenAsync($"{replica}.{headlessService}.{ns}.svc.cluster.local",
                    SqlEndpoints.ContainerPort, saPassword, SqlProfile.Probe, "master", ct);
                var key = $"{ag.Metadata.Uid}/{replica}";
                if (!_provisioned.TryGetValue(key, out var done) || done != marker)
                {
                    await MssqlopSchema.EnsureAsync(conn, ct);
                    await agentManager.ReplaceJobDefinitionAsync(conn, QuorumWatchdog.Definition(name, Budget, mode == SafetyMode.Enforce), ct);
                    _provisioned[key] = marker;
                    logger.LogInformation("AG {Ag}: quorum watchdog provisioned on {Replica} ({Marker})", name, replica, marker);
                }
                var state = await QuorumWatchdog.ReadAsync(conn, name, ct);
                states[replica] = state;
                if (state?.AgeSeconds is { } age)
                    metrics?.WatchdogRan(ns, name, replica, now.AddSeconds(-age));
                if (state is { Role: "PRIMARY", SyncedSecondaries: { } synced })
                    metrics?.SetSyncedSecondaries(ns, name, synced);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG {Ag}: could not provision or read the watchdog of {Replica}", name, replica);
                states[replica] = null;
            }
        }

        foreach (var (replica, state) in states)
            _aliveAtLastRead[$"{ag.Metadata.Uid}/{replica}"] = QuorumWatchdog.Alive(state, Budget);
        ReportHeartbeats(ag, states);
        if (mode == SafetyMode.Shadow)
            CompareWithCurrentPath(ag, health, states, currentOutcome, now);
        return states;
    }

    /// <summary>
    /// The ceiling back at 1, the watchdog jobs kept running on the three replicas —
    /// harmless in Shadow, but one provisioned in Enforce would have gone on taking the AG offline with
    /// level 2 believed off. They are disabled (not deleted: the definition stays for when level 2
    /// returns), on every replica, before the condition is cleared.
    /// </summary>
    private async Task TurnOffWatchdogsAsync(SqlAvailabilityGroup ag, Health.AgHealthReport health, string saPassword,
        string headlessService, CancellationToken ct)
    {
        var conditions = ag.Status?.Conditions;
        if (conditions?.FirstOrDefault(c => c.Type == WatchdogProvisionedCondition) is not { Status: "True" })
            return;
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var replicas = Enumerable.Range(0, ag.Spec.Replicas.Count).Select(i => $"{name}-{i}").ToList();
        var done = 0;
        foreach (var replica in replicas.Where(r => health.ReachableReplicas.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                await using var conn = await connectionFactory.OpenAsync($"{replica}.{headlessService}.{ns}.svc.cluster.local",
                    SqlEndpoints.ContainerPort, saPassword, SqlProfile.Probe, "master", ct);
                if (await agentManager.GetJobIdAsync(conn, QuorumWatchdog.JobName(name), ct) is not null)
                    await agentManager.ReplaceJobDefinitionAsync(conn, QuorumWatchdog.Definition(name, Budget, enforce: false, enabled: false), ct);
                _provisioned.TryRemove($"{ag.Metadata.Uid}/{replica}", out _);
                _aliveAtLastRead.TryRemove($"{ag.Metadata.Uid}/{replica}", out _);
                done++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG {Ag}: could not turn off the watchdog of {Replica}", name, replica);
            }
        }
        if (done == replicas.Count)
        {
            ConditionOps.Set(conditions, WatchdogProvisionedCondition, "False", "LevelOff",
                "Safety level 2 is off: the quorum watchdog job is disabled on every replica.");
            logger.LogInformation("AG {Ag}: level 2 off, quorum watchdog disabled on every replica", name);
        }
    }

    /// <summary>A replica whose watchdog does not run makes T_vote prove nothing.</summary>
    private void ReportHeartbeats(SqlAvailabilityGroup ag, Dictionary<string, WatchdogState?> states)
    {
        var silent = states.Where(s => !QuorumWatchdog.Alive(s.Value, Budget)).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
        var conditions = ag.Status!.Conditions ??= [];
        if (silent.Count > 0)
            ConditionOps.Set(conditions, WatchdogUnavailableCondition, "True", "WatchdogSilent",
                $"The quorum watchdog of {string.Join(", ", silent)} has not run for more than {3 * Budget.WatchdogPeriod.TotalSeconds} s: no " +
                "election may rely on T_vote while it is silent (check SQL Server Agent on those replicas).");
        else if (conditions.FirstOrDefault(c => c.Type == WatchdogUnavailableCondition) is { Status: "True" })
            ConditionOps.Set(conditions, WatchdogUnavailableCondition, "False", "WatchdogRunning", "Every reachable replica's watchdog runs.");
    }

    /// <summary>The observation the core decides on, built from the health read and the watchdogs.
    /// <paramref name="aliveAtLastRead"/>: for a replica not reachable now, whether its watchdog ran
    /// at the last read — what lets T_vote prove that an unreachable old primary stepped down.</summary>
    public static AgObservation Observe(SqlAvailabilityGroup ag, Health.AgHealthReport health, IReadOnlyDictionary<string, WatchdogState?> states,
        long epoch, DateTimeOffset now, IReadOnlyDictionary<string, bool>? aliveAtLastRead = null)
    {
        var replicas = Enumerable.Range(0, ag.Spec.Replicas.Count).Select(i => $"{ag.Metadata.Name}-{i}").Select(name =>
        {
            var reachable = health.ReachableReplicas.Contains(name);
            var info = health.ReplicaStates.FirstOrDefault(r => r.Name == name);
            states.TryGetValue(name, out var watchdog);
            var role = reachable ? info?.Role ?? watchdog?.Role : null;
            var databases = health.DatabaseStates.Where(d => d.ReplicaName == name).ToList();
            var lsn = databases.Where(d => d.LastHardenedLsn is not null)
                .GroupBy(d => d.DatabaseName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(d => d.LastHardenedLsn!.Value), StringComparer.Ordinal);
            return new ReplicaObservation(name, reachable,
                Role: role,
                // An AG taken OFFLINE answers RESOLVING on its replica: it serves nothing.
                Offline: reachable && role == "RESOLVING",
                SynchronousCommit: info?.AvailabilityMode != "ASYNCHRONOUS_COMMIT",
                SeesPrimaryConnected: watchdog is { Role: "SECONDARY", PrimaryDisconnectedSeconds: null },
                PrimaryDisconnectedFor: watchdog?.PrimaryDisconnectedSeconds is { } lost ? TimeSpan.FromSeconds(lost) : null,
                SynchronizedSecondaries: watchdog?.SyncedSecondaries ?? 0,
                Suspended: databases.Count > 0 && databases.All(d => d.IsSuspended),
                HardenedLsn: lsn.Count > 0 ? lsn : null,
                WatchdogAlive: reachable
                    ? QuorumWatchdog.Alive(watchdog, TimingBudget.Default)
                    : aliveAtLastRead?.GetValueOrDefault(name) ?? false);
        }).ToList();
        return new AgObservation(ag.Metadata.Name, epoch, health.RequiredSynchronizedSecondaries ?? ag.Spec.Replicas.RequiredSynchronizedSecondariesToCommit,
            replicas, ag.Status?.PrimaryReplica, ag.Status?.PrimaryReplica, now);
    }

    private void CompareWithCurrentPath(SqlAvailabilityGroup ag, Health.AgHealthReport health, Dictionary<string, WatchdogState?> states,
        string currentOutcome, DateTimeOffset now)
    {
        var alive = Enumerable.Range(0, ag.Spec.Replicas.Count).Select(i => $"{ag.Metadata.Name}-{i}")
            .ToDictionary(r => r, r => _aliveAtLastRead.GetValueOrDefault($"{ag.Metadata.Uid}/{r}"));
        var decision = ElectionCore.Decide(null, Observe(ag, health, states, operatorEpoch?.Current ?? 0, now, alive), Budget);
        var coreActs = decision.Commands.Any(c => c is Isolate or Promote or TakeOffline or MsSqlOperator.Core.Election.Route);
        var currentActs = currentOutcome is not "NoAction";
        if (coreActs != currentActs)
            logger.LogWarning("Level 2 shadow on AG {Ag}: the election core decided [{Core}] ({Reason}), the current path {Current}",
                ag.Metadata.Name, string.Join(", ", decision.Commands.Select(c => c.GetType().Name)), decision.Reason, currentOutcome);
        else
            logger.LogDebug("Level 2 shadow on AG {Ag}: agrees ({Reason})", ag.Metadata.Name, decision.Reason);
    }
}
