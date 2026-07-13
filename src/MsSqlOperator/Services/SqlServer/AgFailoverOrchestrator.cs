using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Drives Availability Group failover for <c>CLUSTER_TYPE = NONE</c> + <c>FAILOVER_MODE = MANUAL</c>
/// groups, where SQL Server performs no automatic promotion and the operator must do it.
///
/// The safety invariant: <b>never promote a target before the old
/// primary is provably unable to write.</b> In a clusterless AG the only promotion command is
/// <c>FORCE_FAILOVER_ALLOW_DATA_LOSS</c>, and it does NOT demote the source — so a source that is
/// still alive stays PRIMARY and keeps accepting writes on established connections and direct pod
/// FQDNs. Every failover path therefore isolates the old primary first:
/// <list type="number">
/// <item>the old primary is reachable → <c>SET (ROLE = SECONDARY)</c>, verified by re-reading its
/// role (the documented Microsoft procedure for clusterless AGs);</item>
/// <item>it is unreachable → confirmed fencing (kubelet-acknowledged deletion on a Ready node, or
/// all VolumeAttachments detached), which is real proof, unlike the previous "the Pod object left
/// etcd" check;</item>
/// <item>neither can be proven → the failover is ABORTED (no promotion, writes stay drained) and a
/// runbook event is emitted. Refusing to act is always safer than two primaries.</item>
/// </list>
/// </summary>
public interface IAgFailoverOrchestrator
{
    Task<FailoverOutcome> ReconcileFailoverAsync(SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, CancellationToken ct);
}

public enum FailoverOutcome
{
    NoAction,
    PlannedFailover,
    ForcedFailover,
    AwaitingManualIntervention,
    Failed,
}

public class AgFailoverOrchestrator(
    ILogger<AgFailoverOrchestrator> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IAgPodManager agPodManager,
    IReplicaFencer replicaFencer,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics,
    TimeProvider timeProvider) : IAgFailoverOrchestrator
{
    public const string FailoverToAnnotation = "mssql-operator.evariops.com/failover-to";
    public const string ForceFailoverToAnnotation = "mssql-operator.evariops.com/force-failover-to";
    public const string AllowDataLossAnnotation = "mssql-operator.evariops.com/allow-data-loss";

    /// <summary>Planned failovers are idempotent and re-verifiable, so their annotation is kept
    /// until the failover lands — but not forever: a target that keeps failing must not be retried
    /// indefinitely.</summary>
    private const int MaxPlannedFailoverAttempts = 3;

    /// <summary>Never act on "no primary" faster than this, whatever the pass count.</summary>
    private static readonly TimeSpan MinUnreachableWindow = TimeSpan.FromSeconds(20);

    /// <summary>The role flip after FORCE_FAILOVER is observable only once the engine finished the
    /// transition. Bounded by attempts (not a wall clock) so the loop is deterministic.</summary>
    private const int RoleConfirmAttempts = 15;
    private static readonly TimeSpan RoleConfirmInterval = TimeSpan.FromSeconds(2);

    public async Task<FailoverOutcome> ReconcileFailoverAsync(SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var failoverSpec = ag.Spec.Failover ?? new AgFailoverSpec();
        ag.Status ??= new SqlAvailabilityGroupStatus();

        var annotations = ag.Metadata.Annotations ?? new Dictionary<string, string>();
        annotations.TryGetValue(FailoverToAnnotation, out var plannedTarget);
        annotations.TryGetValue(ForceFailoverToAnnotation, out var forcedTarget);
        var annotationAllowsDataLoss = annotations.TryGetValue(AllowDataLossAnnotation, out var dl)
                                       && bool.TryParse(dl, out var b) && b;

        // Remember the engine build of the confirmed primary: a failover must never promote a
        // replica running an older major version.
        if (health.PrimaryReplica != null
            && health.ReplicaVersions.TryGetValue(health.PrimaryReplica, out var primaryVersion))
            ag.Status.PrimaryProductVersion = primaryVersion;

        // During a detected split-brain no consumer may act on an arbitrary "primary" —
        // the only safe automatic action is draining the writer path (no listener endpoint ⇒ no
        // divergent writes ⇒ data stays consistent on the rightful primary). Auto-resolution is
        // deliberately NOT attempted: a clusterless AG has no arbiter and picking the wrong
        // survivor would lose data.
        if (health.DetectedPrimaries is { Count: > 1 })
        {
            await eventPublisher.PublishAsync(ag, "SplitBrainDetected",
                $"Multiple primaries detected ({string.Join(", ", health.DetectedPrimaries)}); writes are drained until resolved. {SplitBrainRecoveryGuidance(name, ag.Status.LastFailover, health.DetectedPrimaries)}",
                "Warning", ct);
            await MarkAllResolvingAsync(ag, health, ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        // 1. Explicit forced (data-loss) failover requested by a human.
        if (!string.IsNullOrWhiteSpace(forcedTarget))
        {
            // Consume the annotation BEFORE executing — replaying a data-loss failover
            // because the post-execution clear failed is the failure mode to kill.
            if (!await ClearFailoverAnnotationsAsync(ag, ct))
            {
                await eventPublisher.PublishAsync(ag, "FailoverDeferred",
                    "Could not clear failover annotations; deferring the forced failover to the next cycle", "Warning", ct);
                return FailoverOutcome.NoAction;
            }

            var forcedReplica = ResolveTargetReplica(forcedTarget!, name, health);
            if (forcedReplica == null)
            {
                await eventPublisher.PublishAsync(ag, "FailoverRejected",
                    $"Forced failover target is not a known replica ordinal or name: {forcedTarget}", "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }
            return await ExecuteFailoverAsync(ag, saPassword, health, forcedReplica, allowDataLoss: true, "ForcedFailover", ct);
        }

        // 2. Explicit planned failover requested.
        if (!string.IsNullOrWhiteSpace(plannedTarget))
            return await ReconcilePlannedFailoverAsync(ag, saPassword, health, plannedTarget!, annotationAllowsDataLoss, ct);

        // 3. No primary reachable → automatic failover, if the operator is allowed to manage it.
        if (health.PrimaryReplica == null && failoverSpec.OperatorManaged)
            return await ReconcileUnreachablePrimaryAsync(ag, saPassword, health, failoverSpec, ct);

        if (health.PrimaryReplica != null)
        {
            ag.Status.PrimaryUnreachableCount = 0;
            ag.Status.PrimaryUnreachableSince = null;
        }

        return FailoverOutcome.NoAction;
    }

    // ── Planned failover (annotation) ─────────────────────

    private async Task<FailoverOutcome> ReconcilePlannedFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health,
        string plannedTarget, bool annotationAllowsDataLoss, CancellationToken ct)
    {
        var name = ag.Metadata.Name;

        var plannedReplica = ResolveTargetReplica(plannedTarget, name, health);
        if (plannedReplica == null)
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            await eventPublisher.PublishAsync(ag, "FailoverRejected",
                $"Planned failover target is not a known replica ordinal or name: {plannedTarget}", "Warning", ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        // Already there — the annotation is satisfied (this also makes a replayed planned failover
        // a no-op instead of a FORCE_FAILOVER against the current primary).
        if (plannedReplica.Equals(health.PrimaryReplica, StringComparison.OrdinalIgnoreCase))
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            ag.Status!.PendingFailover = null;
            await eventPublisher.PublishAsync(ag, "FailoverNoop",
                $"Planned failover to {plannedReplica} skipped: it is already the primary", ct: ct);
            return FailoverOutcome.NoAction;
        }

        var target = health.ReplicaStates.FirstOrDefault(r =>
            r.Name.Equals(plannedReplica, StringComparison.OrdinalIgnoreCase));
        if (!annotationAllowsDataLoss && !IsLosslessTarget(target, health, out var notReadyReason))
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            await eventPublisher.PublishAsync(ag, "FailoverRejected",
                $"Planned failover to {plannedReplica} rejected: {notReadyReason}. " +
                $"Set {AllowDataLossAnnotation}=true and use {ForceFailoverToAnnotation} to force.", "Warning", ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        if (RejectOnVersionRegression(ag, plannedReplica, health) is { } versionReject)
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            await eventPublisher.PublishAsync(ag, "FailoverRejected", versionReject, "Warning", ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        // The annotation is NOT consumed up front on the planned path — a transient
        // connection error to the target used to make the failover request vanish silently. The
        // intent is tracked in the status and retried, then abandoned loudly.
        var pending = ag.Status!.PendingFailover;
        if (pending is null || !string.Equals(pending.Target, plannedReplica, StringComparison.OrdinalIgnoreCase))
            pending = ag.Status.PendingFailover = new AgPendingFailoverStatus
            {
                Target = plannedReplica,
                RequestedAt = timeProvider.GetUtcNow().UtcDateTime,
                Attempts = 0,
            };

        pending.Attempts++;

        var outcome = await ExecuteFailoverAsync(ag, saPassword, health, plannedReplica,
            allowDataLoss: annotationAllowsDataLoss, "PlannedFailover", ct);

        if (outcome is FailoverOutcome.PlannedFailover or FailoverOutcome.ForcedFailover)
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            ag.Status.PendingFailover = null;
            return outcome;
        }

        if (pending.Attempts >= MaxPlannedFailoverAttempts)
        {
            await ClearFailoverAnnotationsAsync(ag, ct);
            ag.Status.PendingFailover = null;
            await eventPublisher.PublishAsync(ag, "FailoverAbandoned",
                $"Planned failover to {plannedReplica} failed {pending.Attempts} times; giving up. " +
                "Resolve the cause and re-annotate to retry.", "Warning", ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        await eventPublisher.PublishAsync(ag, "FailoverRetryScheduled",
            $"Planned failover to {plannedReplica} did not complete (attempt {pending.Attempts}/{MaxPlannedFailoverAttempts}); will retry", "Warning", ct);
        return outcome;
    }

    // ── Automatic failover (no primary reachable) ─────────

    private async Task<FailoverOutcome> ReconcileUnreachablePrimaryAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, AgFailoverSpec failoverSpec, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ag.Status!;

        // A pass counter alone is not a delay. Every increment writes the
        // status, which fires a watch event, which re-enqueues the AG immediately — three "passes"
        // could elapse in ~20s, so a brief operator↔primary network blip was enough to force a
        // promotion. The threshold is now a wall-clock window as well: BOTH the pass count and the
        // elapsed time since the first missed pass must be satisfied.
        status.PrimaryUnreachableSince ??= now;
        var threshold = Math.Max(1, failoverSpec.PrimaryUnreachableThreshold);
        if (status.PrimaryUnreachableCount < threshold)
            status.PrimaryUnreachableCount++;

        var interval = TimeSpan.FromSeconds(Math.Max(1, failoverSpec.HealthCheckIntervalSeconds));
        var window = TimeSpan.FromSeconds(Math.Max(
            MinUnreachableWindow.TotalSeconds, threshold * interval.TotalSeconds));
        var elapsed = now - status.PrimaryUnreachableSince.Value;

        if (status.PrimaryUnreachableCount < threshold || elapsed < window)
        {
            await eventPublisher.PublishAsync(ag, "PrimaryUnreachable",
                $"No primary reachable ({status.PrimaryUnreachableCount}/{threshold} passes, " +
                $"{(int)elapsed.TotalSeconds}s/{(int)window.TotalSeconds}s); deferring failover action", "Warning", ct);
            return FailoverOutcome.NoAction;
        }

        return await AutoFailoverAsync(ag, saPassword, health, failoverSpec, ct);
    }

    private async Task<FailoverOutcome> AutoFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, AgFailoverSpec failoverSpec, CancellationToken ct)
    {
        var name = ag.Metadata.Name;

        // Close the write window immediately: with no confirmed primary, no pod may keep the
        // writer label. The R/W listener selector requires listener-ready=writer,
        // so this drains it until a new primary is confirmed.
        await MarkAllResolvingAsync(ag, health, ct);

        // Pick a synchronized, failover-ready secondary — a lossless promotion target.
        var candidate = SelectTarget(ag, health, failoverSpec, requireFailoverReady: true);
        if (candidate != null)
        {
            await eventPublisher.PublishAsync(ag, "AutoFailoverStarted",
                $"No primary detected; promoting failover-ready secondary {candidate.Name} (no data loss)", "Warning", ct);
            return await ExecuteFailoverAsync(ag, saPassword, health, candidate.Name, allowDataLoss: false, "AutoFailover", ct);
        }

        // No synchronized target. Only a data-loss failover can recover — and only if explicitly allowed.
        if (failoverSpec.AllowAutomaticDataLossFailover)
        {
            // Mixed-version windows are the most dangerous moment to force-promote —
            // an un-upgraded replica cannot redo the log of an upgraded primary, and promoting it
            // strands every write made since the upgrade. While a rollout is in flight (or stopped
            // in Failed), the automatic data-loss path is disarmed and a human must decide.
            var rollout = ag.Status?.RollingUpdate?.Phase;
            if (rollout is not (null or "Idle"))
            {
                await eventPublisher.PublishAsync(ag, "AutoForceFailoverBlocked",
                    $"AG {name} has no synchronized secondary and a rolling update is in progress (phase={rollout}): " +
                    "the automatic data-loss failover is disarmed because replicas may run different SQL Server builds. " +
                    $"Inspect the replicas, then set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force.",
                    "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            var forcedCandidate = SelectTarget(ag, health, failoverSpec, requireFailoverReady: false);
            if (forcedCandidate != null)
            {
                // An automatic promotion that accepts data loss must accept a BOUNDED
                // amount of it. maxDataLossKb turns "some transactions may be lost" into a decision
                // the user made explicitly.
                var lagKb = forcedCandidate.LagKb;
                if (failoverSpec.MaxDataLossKb is { } maxKb && (lagKb is null || lagKb > maxKb))
                {
                    await eventPublisher.PublishAsync(ag, "AutoForceFailoverBlocked",
                        $"Automatic data-loss failover to {forcedCandidate.Name} blocked: unsent log is " +
                        $"{(lagKb is null ? "unknown" : lagKb + " KB")}, above the configured maxDataLossKb={maxKb}. " +
                        $"Set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force it anyway.",
                        "Warning", ct);
                    return FailoverOutcome.AwaitingManualIntervention;
                }

                await eventPublisher.PublishAsync(ag, "AutoForceFailoverStarted",
                    $"No synchronized secondary; forcing failover to {forcedCandidate.Name} with possible data loss " +
                    $"(unsent log: {(lagKb is null ? "unknown" : lagKb + " KB")}, allowAutomaticDataLossFailover=true)", "Warning", ct);
                return await ExecuteFailoverAsync(ag, saPassword, health, forcedCandidate.Name,
                    allowDataLoss: true, "AutoForceFailover", ct, forcedCandidate.LagKb);
            }
        }

        await eventPublisher.PublishAsync(ag, "FailoverNeedsIntervention",
            $"AG {name} has no primary and no synchronized failover-ready secondary. " +
            $"Manual intervention required: set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force.",
            "Warning", ct);
        return FailoverOutcome.AwaitingManualIntervention;
    }

    // ── Target selection ──────────────────────────────────

    private sealed record TargetCandidate(string Name, bool Synchronous, int ReadyDatabases, int SeedingDatabases, long? LagKb);

    /// <summary>
    /// Chooses the replica to promote.
    ///
    /// Eligibility is decided per DATABASE, not on the replica-level
    /// <c>is_failover_ready</c> flag, which SQL Server aggregates with MIN over every database —
    /// so a single database being seeded (hours, on a brand-new SAGDB) marked the whole replica
    /// unusable and silently disarmed lossless failover for the entire AG. Databases still being
    /// seeded (INITIALIZING / RESTORING — never synchronized on this replica yet) do not block a
    /// promotion: their only copy lives on the primary either way and they are simply re-seeded.
    /// A database that WAS synchronized and is now behind does block it.
    /// </summary>
    private TargetCandidate? SelectTarget(SqlAvailabilityGroup ag, AgHealthReport health, AgFailoverSpec failoverSpec, bool requireFailoverReady)
    {
        var candidates = new List<TargetCandidate>();

        foreach (var r in health.ReplicaStates)
        {
            if (r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsVersionRegression(ag, r.Name, health))
                continue;

            var dbs = health.DatabaseStates
                .Where(d => d.ReplicaName.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var seeding = dbs.Count(IsSeeding);
            var ready = dbs.Count(d => d.IsFailoverReady && IsSynchronized(d));
            var stale = dbs.Count(d => !IsSeeding(d) && !(d.IsFailoverReady && IsSynchronized(d)));

            if (requireFailoverReady)
            {
                if (dbs.Count == 0)
                {
                    // No per-database view (an AG with no databases yet, or a fallback report from a
                    // secondary that could not read them): fall back to the engine's replica flag.
                    if (!r.IsFailoverReady)
                        continue;
                }
                else if (stale > 0 || ready == 0)
                {
                    // Lossless promotion: every database that ever synchronized must be failover-ready.
                    continue;
                }

                // The engine's replica-level health is PARTIALLY_HEALTHY while a database seeds:
                // only a hard NOT_HEALTHY (a real desync) disqualifies the replica.
                if (r.SynchronizationHealth.Equals("NOT_HEALTHY", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // Unsent log of the databases this replica would take over, in KB.
            long? lagKb = dbs.Count == 0 ? null : dbs.Sum(d => d.LogSendQueueSize + d.RedoQueueSize);
            candidates.Add(new TargetCandidate(
                r.Name,
                r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase),
                ready, seeding, lagKb));
        }

        if (candidates.Count == 0)
            return null;

        // Honour preferred-target ordinals first.
        if (failoverSpec.PreferredTargets is { Count: > 0 })
        {
            foreach (var pref in failoverSpec.PreferredTargets)
            {
                var match = candidates.FirstOrDefault(c => c.Name.EndsWith($"-{pref.Ordinal}", StringComparison.Ordinal));
                if (match != null) return match;
            }
        }

        if (requireFailoverReady)
        {
            // All are synchronized: prefer one with nothing left to re-seed, then lowest ordinal
            // for determinism.
            return candidates
                .OrderBy(c => c.SeedingDatabases)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .First();
        }

        // A forced promotion accepts data loss — minimize it. Synchronous
        // secondaries first, then the most up-to-date copies (most failover-ready databases,
        // smallest replication queues). An unknown queue is treated as the WORST case, never 0.
        return candidates
            .OrderByDescending(c => c.Synchronous)
            .ThenByDescending(c => c.ReadyDatabases)
            .ThenBy(c => c.LagKb ?? long.MaxValue)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .First();
    }

    /// <summary>A database whose copy on this replica has never been synchronized: it is being
    /// seeded (or was just added). Losing it costs nothing that the primary does not already own.</summary>
    private static bool IsSeeding(AgDatabaseStateInfo d) =>
        Normalize(d.SynchronizationState) is "INITIALIZING" or "RESTORING" or "REVERTING";

    private static bool IsSynchronized(AgDatabaseStateInfo d) =>
        Normalize(d.SynchronizationState) is "SYNCHRONIZED";

    /// <summary>DMV values use spaces ("NOT SYNCHRONIZING"); older code compared underscores.</summary>
    private static string Normalize(string s) => s.Replace('_', ' ').Trim().ToUpperInvariant() switch
    {
        "NOT SYNCHRONIZING" => "NOT SYNCHRONIZING",
        var v => v,
    };

    private bool IsLosslessTarget(AgReplicaStateInfo? target, AgHealthReport health, out string reason)
    {
        if (target is null)
        {
            reason = "the replica is not present in the current health report";
            return false;
        }

        var dbs = health.DatabaseStates
            .Where(d => d.ReplicaName.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var stale = dbs.Where(d => !IsSeeding(d) && !(d.IsFailoverReady && IsSynchronized(d))).ToList();
        if (stale.Count > 0)
        {
            reason = $"databases not synchronized on this replica: {string.Join(", ", stale.Select(d => d.DatabaseName))}";
            return false;
        }

        if (dbs.Count == 0 && !target.IsFailoverReady)
        {
            reason = "replica is not failover-ready (not synchronized)";
            return false;
        }

        if (target.SynchronizationHealth.Equals("NOT_HEALTHY", StringComparison.OrdinalIgnoreCase))
        {
            reason = "replica synchronization health is NOT_HEALTHY";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // ── Version guard ───────────────

    private bool IsVersionRegression(SqlAvailabilityGroup ag, string replica, AgHealthReport health)
    {
        var lastPrimary = ag.Status?.PrimaryProductVersion;
        if (string.IsNullOrEmpty(lastPrimary))
            return false;
        if (!health.ReplicaVersions.TryGetValue(replica, out var candidate) || string.IsNullOrEmpty(candidate))
            return false;
        return MajorVersion(candidate) < MajorVersion(lastPrimary);
    }

    private string? RejectOnVersionRegression(SqlAvailabilityGroup ag, string replica, AgHealthReport health)
    {
        if (!IsVersionRegression(ag, replica, health))
            return null;
        health.ReplicaVersions.TryGetValue(replica, out var candidate);
        return $"failover to {replica} rejected: it runs SQL Server {candidate} while the databases were last " +
               $"primary on {ag.Status?.PrimaryProductVersion}. Promoting an older build cannot redo the newer " +
               "log and would strand every write made since the upgrade. Finish the rolling update first.";
    }

    private static int MajorVersion(string productVersion)
    {
        var dot = productVersion.IndexOf('.');
        var head = dot > 0 ? productVersion[..dot] : productVersion;
        return int.TryParse(head, out var major) ? major : 0;
    }

    // ── Execution ─────────────────────────────────────────

    private async Task<FailoverOutcome> ExecuteFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, string targetReplica,
        bool allowDataLoss, string reason, CancellationToken ct, long? lagKb = null)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var targetFqdn = $"{targetReplica}.{headlessService}.{ns}.svc.cluster.local";

        var oldPrimary = health.PrimaryReplica ?? ag.Status?.PrimaryReplica;
        if (oldPrimary != null && oldPrimary.Equals(targetReplica, StringComparison.OrdinalIgnoreCase))
            oldPrimary = null;

        try
        {
            // Open the target connection FIRST: a promotion we cannot even reach must not leave the
            // old primary de-routed or fenced.
            await using var conn = await connectionFactory.CreateConnectionAsync(
                targetFqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            if (oldPrimary != null)
            {
                // De-route the last known primary even when it is absent from the reachable set: a
                // partitioned-but-alive primary kept its writer label and kept accepting writes
                // through the listener. Label patches are k8s API calls — they work
                // regardless of the pod's SQL reachability.
                await agPodManager.SetPodRoleLabelAsync(oldPrimary, ns, name, "resolving", ct);
                await agPodManager.SetReadabilityLabelsAsync(oldPrimary, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

                var isolation = await IsolateOldPrimaryAsync(ag, oldPrimary, saPassword, ct);
                if (!isolation.Confirmed)
                {
                    await eventPublisher.PublishAsync(ag, "FailoverAborted",
                        $"Failover of AG {name} to {targetReplica} ABORTED: the previous primary {oldPrimary} could not be " +
                        $"demoted nor fenced ({isolation.Detail}). Promoting now would create two primaries and diverge writes. " +
                        "Writes stay drained; stop the old primary (or fence its node) and retry.", "Warning", ct);
                    logger.LogError("Failover of AG {Ag} to {Target} aborted: {OldPrimary} not isolated — {Detail}",
                        name, targetReplica, oldPrimary, isolation.Detail);
                    return FailoverOutcome.Failed;
                }

                logger.LogWarning("AG {Ag}: previous primary {OldPrimary} isolated ({Method}: {Detail})",
                    name, oldPrimary, isolation.Method, isolation.Detail);
            }

            // In a clusterless AG this is the ONLY promotion command — lossless or not depends on
            // the state of the target, which the caller has already gated.
            if (allowDataLoss)
                await agManager.ForceFailoverWithDataLossAsync(conn, name, ct);
            else
                await agManager.FailoverAsync(conn, name, ct);

            metrics.AgFailover(name);

            // The ALTER returns before the role flip is observable. Confirm role_desc =
            // PRIMARY on the target before promoting labels and status — otherwise a failed
            // post-command transition leaves a lying writer label.
            var promoted = false;
            for (var attempt = 0; attempt < RoleConfirmAttempts; attempt++)
            {
                var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
                if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                {
                    promoted = true;
                    break;
                }
                await Task.Delay(RoleConfirmInterval, ct);
            }
            if (!promoted)
            {
                await eventPublisher.PublishAsync(ag, "FailoverUnconfirmed",
                    $"Failover command issued but {targetReplica} did not report PRIMARY; labels/status left untouched", "Warning", ct);
                return FailoverOutcome.Failed;
            }

            // Promote labels immediately so the listener follows the new primary without waiting
            // for the next health cycle.
            await agPodManager.SetPodRoleLabelAsync(targetReplica, ns, name, "primary", ct);
            await agPodManager.SetReadabilityLabelsAsync(targetReplica, ns, readable: false, readIntentReady: false, listenerReady: "writer", ct);

            ag.Status ??= new SqlAvailabilityGroupStatus();
            ag.Status.LastFailover = new AgLastFailoverStatus
            {
                Timestamp = timeProvider.GetUtcNow().UtcDateTime,
                From = oldPrimary,
                To = targetReplica,
                Reason = reason,
                DataLoss = allowDataLoss,
                // Record how much log the promoted replica had NOT received, so the
                // blast radius of a forced failover is auditable after the fact instead of unknown.
                UnsentLogKb = allowDataLoss ? lagKb : 0,
            };
            ag.Status.PrimaryReplica = targetReplica;
            ag.Status.PrimaryUnreachableCount = 0;
            ag.Status.PrimaryUnreachableSince = null;
            if (health.ReplicaVersions.TryGetValue(targetReplica, out var newVersion))
                ag.Status.PrimaryProductVersion = newVersion;

            await eventPublisher.PublishAsync(ag, allowDataLoss ? "ForcedFailoverCompleted" : "FailoverCompleted",
                $"AG {name} {(allowDataLoss ? "force-" : "")}failed over to {targetReplica}" +
                (allowDataLoss && lagKb is { } kb ? $" (unsent log at promotion: {kb} KB)" : ""), ct: ct);
            logger.LogWarning("AG {Ag} {Kind}failover to {Target} completed", name, allowDataLoss ? "force-" : "", targetReplica);

            return allowDataLoss ? FailoverOutcome.ForcedFailover : FailoverOutcome.PlannedFailover;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failover of AG {Ag} to {Target} failed", name, targetReplica);
            await eventPublisher.PublishAsync(ag, "FailoverFailed",
                $"Failover of AG {name} to {targetReplica} failed: {ex.Message}", "Warning", ct);
            return FailoverOutcome.Failed;
        }
    }

    /// <summary>
    /// Makes the old primary provably unable to write, or reports that it could not.
    /// Demotion is tried first because it is the graceful, lossless path and works
    /// for every planned failover and rolling update; fencing is the fallback for a primary the
    /// operator cannot reach — the case where "it is probably dead" used to be assumed.
    /// </summary>
    private async Task<FenceResult> IsolateOldPrimaryAsync(
        SqlAvailabilityGroup ag, string oldPrimary, string saPassword, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var fqdn = $"{oldPrimary}.{name}-headless.{ns}.svc.cluster.local";

        // 1. Reachable → demote it. This is the documented clusterless procedure and leaves the
        //    replica ready to rejoin as a secondary.
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (!string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                return new FenceResult(FenceMethod.Demoted,
                    $"{oldPrimary} does not hold the primary role (role={role ?? "none"})");

            // ALTER AVAILABILITY GROUP … SET (ROLE = SECONDARY) is synchronous: when it returns, the
            // role has flipped. Re-read it once — trust nothing, verify.
            await agManager.DemoteToSecondaryAsync(conn, name, ct);

            role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (!string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                await eventPublisher.PublishAsync(ag, "PrimaryDemoted",
                    $"Previous primary {oldPrimary} demoted to SECONDARY before promoting the new primary", "Warning", ct);
                return new FenceResult(FenceMethod.Demoted, $"{oldPrimary} demoted to SECONDARY and confirmed");
            }

            logger.LogWarning("AG {Ag}: {OldPrimary} still reports PRIMARY after SET (ROLE = SECONDARY); falling back to fencing", name, oldPrimary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AG {Ag}: previous primary {OldPrimary} is not reachable for demotion; fencing instead", name, oldPrimary);
        }

        // 2. Unreachable (or demotion refused) → fencing must PROVE it cannot write.
        var strategy = ag.Spec.Failover?.SplitBrainProtection?.FencingStrategy ?? "NetworkIsolation";
        if (!strategy.Equals("NetworkIsolation", StringComparison.OrdinalIgnoreCase))
        {
            await eventPublisher.PublishAsync(ag, "FencingSkipped",
                $"Fencing strategy '{strategy}' requires external/manual action for {oldPrimary}; the operator will not promote until it is confirmed down",
                "Warning", ct);
            return FenceResult.No($"fencingStrategy={strategy} delegates fencing to an external actor");
        }

        var result = await replicaFencer.FenceAsync(oldPrimary, ns, ct);
        if (result.Confirmed)
            await eventPublisher.PublishAsync(ag, "ReplicaFenced",
                $"Previous primary {oldPrimary} fenced before failover ({result.Method}: {result.Detail})", "Warning", ct);
        else
            await eventPublisher.PublishAsync(ag, "FencingFailed",
                $"Could not fence previous primary {oldPrimary}: {result.Detail}", "Warning", ct);
        return result;
    }

    // ── Helpers ───────────────────────────────────────────

    /// <summary>
    /// Builds a safe, precise recovery runbook for a detected split-brain. When the operator
    /// recorded the last failover and both its endpoints are among the detected primaries, the
    /// replica it failed AWAY from is the stale primary that came back — name it and the survivor so
    /// recovery is a known procedure rather than guesswork. Emits no SQL: operator-facing guidance.
    /// </summary>
    private static string SplitBrainRecoveryGuidance(string agName, AgLastFailoverStatus? lastFailover, IEnumerable<string> detectedPrimaries)
    {
        var detected = detectedPrimaries.ToList();
        if (lastFailover?.From is { } from && lastFailover.To is { } to
            && detected.Contains(from, StringComparer.OrdinalIgnoreCase)
            && detected.Contains(to, StringComparer.OrdinalIgnoreCase))
        {
            return $"Likely survivor: {to} (last promoted, holds the committed data). Recovery: on {to}, " +
                   $"REMOVE REPLICA {from} from availability group {agName}; then on {from} drop the stale " +
                   $"availability group and its databases so the operator re-seeds it as a secondary.";
        }
        return $"No safe survivor could be inferred for availability group {agName} (no recorded failover); " +
               "choose the replica with the latest committed data as the survivor and evict the other.";
    }

    /// <summary>
    /// Resolves a failover-to / force-failover-to annotation value to a concrete replica
    /// (replica_server_name). The documented user contract is an ordinal (<c>failover-to: "1"</c>),
    /// but the internal rolling-update orchestrator writes a replica name — accept both. The
    /// resolved name must belong to a replica in the current health report; otherwise returns null
    /// so the caller rejects cleanly instead of building an unresolvable FQDN.
    /// </summary>
    private static string? ResolveTargetReplica(string value, string agName, AgHealthReport health)
    {
        value = value.Trim();

        var byName = health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName.Name;

        if (int.TryParse(value, out var ordinal) && ordinal >= 0)
        {
            var podName = $"{agName}-{ordinal}";
            var byOrdinal = health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(podName, StringComparison.OrdinalIgnoreCase));
            if (byOrdinal != null) return byOrdinal.Name;
        }

        return null;
    }

    /// <summary>
    /// Drain the <em>write</em> path while there is no confirmed primary: every reachable pod loses
    /// <c>listener-ready=writer</c>, so the R/W listener (selector requires it) has no backing pod.
    /// Crucially this does NOT take healthy secondaries out of the read path — they stay
    /// <c>ag-role=secondary</c> with their readability labels, so read traffic keeps flowing during
    /// the failover. Only pods still reporting <c>PRIMARY</c> (the stale/divergent old primary, or a
    /// split-brain) are forced to <c>resolving</c> and fully de-routed.
    /// </summary>
    private async Task MarkAllResolvingAsync(SqlAvailabilityGroup ag, AgHealthReport health, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var name = ag.Metadata.Name;
        foreach (var replica in health.ReachableReplicas)
        {
            try
            {
                var state = health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(replica, StringComparison.OrdinalIgnoreCase));
                var reportsPrimary = state != null && state.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);

                if (state is null || reportsPrimary)
                {
                    // Ambiguous or stale primary → fence both paths.
                    await agPodManager.SetPodRoleLabelAsync(replica, ns, name, "resolving", ct);
                    await agPodManager.SetReadabilityLabelsAsync(replica, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);
                }
                else
                {
                    // Confirmed secondary → keep serving reads; only strip any writer designation.
                    var (readable, readIntentReady, listenerReady) = AgReadRouting.Compute(state, isPrimary: false);
                    await agPodManager.SetPodRoleLabelAsync(replica, ns, name, "secondary", ct);
                    await agPodManager.SetReadabilityLabelsAsync(replica, ns, readable, readIntentReady, listenerReady, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not drain write path on {Replica}", replica);
            }
        }
    }

    /// <summary>Removes the failover annotations and persists. Returns false when the write
    /// failed — on the forced path the caller must NOT execute the failover (the previous
    /// swallow allowed a data-loss failover to replay on every cycle).</summary>
    private async Task<bool> ClearFailoverAnnotationsAsync(SqlAvailabilityGroup ag, CancellationToken ct)
    {
        if (ag.Metadata.Annotations == null) return true;
        var changed = ag.Metadata.Annotations.Remove(FailoverToAnnotation)
                      | ag.Metadata.Annotations.Remove(ForceFailoverToAnnotation)
                      | ag.Metadata.Annotations.Remove(AllowDataLossAnnotation);
        if (!changed) return true;
        try
        {
            var updated = await kubernetesClient.UpdateAsync(ag, ct);
            // Keep resourceVersion fresh so a subsequent status update doesn't 409.
            ag.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear failover annotations on AG {Ag}", ag.Metadata.Name);
            return false;
        }
    }
}
