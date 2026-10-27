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

    /// <summary>The previous primary is being fenced; the promotion follows on a later pass.</summary>
    FencingInProgress,
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
        // The rolling update requests its planned failover through the status.
        if (string.IsNullOrWhiteSpace(plannedTarget))
            plannedTarget = ag.Status.RollingUpdate?.FailoverTarget;

        // Remember the engine build of the confirmed primary: a failover must never promote a
        // replica running an older major version.
        if (health.PrimaryReplica != null
            && health.ReplicaVersions.TryGetValue(health.PrimaryReplica, out var primaryVersion))
            ag.Status.PrimaryProductVersion = primaryVersion;

        RecordPrimaryView(ag, health);

        // A fencing started on an earlier pass is driven to its end before anything else.
        if (ag.Status.Fencing is { } fencing)
            return await ContinueFencedFailoverAsync(ag, saPassword, health, fencing, ct);

        // During a detected split-brain no consumer may act on an arbitrary "primary" —
        // the only safe automatic action is draining the writer path (no listener endpoint ⇒ no
        // divergent writes ⇒ data stays consistent on the rightful primary). Auto-resolution is
        // deliberately NOT attempted: a clusterless AG has no arbiter and picking the wrong
        // survivor would lose data.
        if (health.DetectedPrimaries is { Count: > 1 })
        {
            // The resurrection of a fenced/failed-over old primary is the ONE
            // split-brain with a recorded, safe resolution. Fencing proves isolation at promotion
            // time only — the StatefulSet recreates the pod, which boots PRIMARY again from its own
            // AG metadata (nothing demoted it: it was unreachable). Draining BOTH primaries here
            // turned every successful automatic failover into a write outage the moment the old pod
            // came back. When the two detected primaries are exactly the From/To of the last
            // recorded failover, demote From — the operator's own record names the survivor. Any
            // other shape keeps the conservative drain: no arbiter, no guessing.
            if (await TryDemoteResurrectedPrimaryAsync(ag, health, saPassword, ct))
                return FailoverOutcome.NoAction;

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

        // When the rolling update itself just deleted the primary pod
        // (failoverBeforePrimaryUpdate=false), its unavailability is deliberate and bounded by the
        // rollout's own per-pod timeout. Arming the automatic failover here raced the restart: a SQL
        // crash recovery outlives the unreachable window, the fencer then "proved" the death of a pod
        // that was merely booting, promoted a secondary — and the restarted pod came back PRIMARY, a
        // manufactured split-brain. The counters are not advanced either, so the full window applies
        // once the rollout finishes or fails.
        if (status.RollingUpdate is { Phase: "UpdatingPrimary", CurrentPod: not null })
        {
            await eventPublisher.PublishAsync(ag, "AutoFailoverSuspendedDuringPrimaryUpdate",
                $"No primary reachable, but the rolling update is restarting the primary in place " +
                $"({status.RollingUpdate.CurrentPod}); automatic failover stays disarmed until the rollout completes or fails.",
                ct: ct);
            return FailoverOutcome.NoAction;
        }

        // A replica that answered and refused the operator's login is alive — typically an
        // sa password rotation in progress. It must never count as a dead primary.
        if (health.AuthFailures.Count > 0)
        {
            await eventPublisher.PublishAsync(ag, "OperatorLoginRefused",
                $"Replica(s) {string.Join(", ", health.AuthFailures)} refused the operator's sa login: they are alive, so no " +
                "automatic failover is armed. Converge the sa password (rotation in progress?).", "Warning", ct);
            return FailoverOutcome.NoAction;
        }

        // The secondaries' own sessions say whether the primary is alive. A secondary
        // still CONNECTED is receiving its log: the primary is up and only the operator cannot reach
        // it (partition on the operator's side, saturation). Failing over then killed a working
        // primary and emptied the listener for nothing.
        var stillConnected = health.ReplicaStates
            .Where(r => !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                        && health.ReachableReplicas.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
                        && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToList();
        if (stillConnected.Count > 0)
        {
            status.PrimaryUnreachableCount = 0;
            status.PrimaryUnreachableSince = null;
            await eventPublisher.PublishAsync(ag, "PrimaryUnreachableFromOperator",
                $"The operator cannot reach the primary, but {string.Join(", ", stillConnected)} is still connected to it: " +
                "the primary is alive, no failover is attempted.", "Warning", ct);
            return FailoverOutcome.NoAction;
        }

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

        var decision = SelectTargetWithoutPrimary(ag, health, failoverSpec);
        if (decision is { Lossless: true })
        {
            await eventPublisher.PublishAsync(ag, "AutoFailoverStarted",
                $"No primary detected; promoting {decision.Name} (no data loss: {decision.Reason})", "Warning", ct);
            return await ExecuteFailoverAsync(ag, saPassword, health, decision.Name, allowDataLoss: false, "AutoFailover", ct, lagKb: 0);
        }

        // No provably lossless target. Only a data-loss failover can recover — and only if explicitly allowed.
        if (decision != null && failoverSpec.AllowAutomaticDataLossFailover)
        {
            // Mixed-version windows are the most dangerous moment to force-promote —
            // an un-upgraded replica cannot redo the log of an upgraded primary, and promoting it
            // strands every write made since the upgrade. While a rollout is in flight (or stopped
            // in Failed), the automatic data-loss path is disarmed and a human must decide.
            var rollout = ag.Status?.RollingUpdate?.Phase;
            if (rollout is not (null or "Idle"))
            {
                await eventPublisher.PublishAsync(ag, "AutoForceFailoverBlocked",
                    $"AG {name} has no provably lossless failover target and a rolling update is in progress (phase={rollout}): " +
                    "the automatic data-loss failover is disarmed because replicas may run different SQL Server builds. " +
                    $"Inspect the replicas, then set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force.",
                    "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            // An automatic promotion that accepts data loss must accept a BOUNDED amount
            // of it. Without the primary, the log it never sent is unknowable: a configured bound
            // cannot be honoured, so it blocks.
            if (failoverSpec.MaxDataLossKb is { } maxKb)
            {
                await eventPublisher.PublishAsync(ag, "AutoForceFailoverBlocked",
                    $"Automatic data-loss failover to {decision.Name} blocked: without the primary, the amount of unreplicated log " +
                    $"cannot be measured, so maxDataLossKb={maxKb} cannot be guaranteed ({decision.Reason}). " +
                    $"Set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force it anyway.",
                    "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            await eventPublisher.PublishAsync(ag, "AutoForceFailoverStarted",
                $"No provably lossless target; forcing failover to {decision.Name} with possible data loss " +
                $"({decision.Reason}; allowAutomaticDataLossFailover=true)", "Warning", ct);
            return await ExecuteFailoverAsync(ag, saPassword, health, decision.Name,
                allowDataLoss: true, "AutoForceFailover", ct);
        }

        await eventPublisher.PublishAsync(ag, "FailoverNeedsIntervention",
            $"AG {name} has no primary and no provably lossless failover target{(decision is null ? "" : $" ({decision.Reason})")}. " +
            $"Manual intervention required: set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force.",
            "Warning", ct);
        return FailoverOutcome.AwaitingManualIntervention;
    }

    // ── Target selection without a primary ────────

    private sealed record PromotionDecision(string Name, bool Lossless, string Reason);

    /// <summary>
    /// Chooses the replica to promote when the primary is gone.
    ///
    /// The previous selection required SYNCHRONIZED, failover-ready databases from each secondary's
    /// own view — but a secondary that lost its primary reports NOT SYNCHRONIZING, so no candidate
    /// ever qualified on a real primary loss (and one always did when the primary was merely
    /// invisible to the operator). The documented procedure for CLUSTER_TYPE = NONE is used instead:
    /// promote the survivor holding the most log, compared by <c>last_hardened_lsn</c> per database.
    /// The promotion is provably lossless when every commit needed a synchronized secondary
    /// (REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT ≥ 1, last read on the primary), every replica that
    /// was synchronized at the last primary-sourced observation survived, and the candidate holds the
    /// highest LSN of every database: each commit is then on the chosen replica.
    /// </summary>
    private PromotionDecision? SelectTargetWithoutPrimary(SqlAvailabilityGroup ag, AgHealthReport health, AgFailoverSpec failoverSpec)
    {
        var candidates = health.ReplicaStates
            .Where(r => !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                        && health.ReachableReplicas.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
                        && !IsVersionRegression(ag, r.Name, health))
            .ToList();
        if (candidates.Count == 0)
            return null;

        bool IsCandidate(string replica) => candidates.Any(c => c.Name.Equals(replica, StringComparison.OrdinalIgnoreCase));
        var rows = health.DatabaseStates.Where(d => IsCandidate(d.ReplicaName)).ToList();
        var databases = rows.Select(d => d.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var maxLsn = databases.ToDictionary(
            db => db,
            db => rows.Where(d => d.DatabaseName.Equals(db, StringComparison.OrdinalIgnoreCase) && d.LastHardenedLsn is not null)
                .Select(d => d.LastHardenedLsn!.Value)
                .DefaultIfEmpty(0)
                .Max(),
            StringComparer.OrdinalIgnoreCase);

        int DatabasesAtMax(string replica) => databases.Count(db =>
            rows.FirstOrDefault(d => d.ReplicaName.Equals(replica, StringComparison.OrdinalIgnoreCase)
                                     && d.DatabaseName.Equals(db, StringComparison.OrdinalIgnoreCase)) is { LastHardenedLsn: { } lsn }
            && lsn >= maxLsn[db]);

        var synchronizedAtLoss = ag.Status?.LastSynchronizedReplicas ?? [];
        var requiredSync = ag.Status?.LastRequiredSynchronizedSecondaries ?? 0;
        var preferred = failoverSpec.PreferredTargets?.Select(p => $"{ag.Metadata.Name}-{p.Ordinal}").ToList() ?? [];

        var best = candidates
            .OrderByDescending(c => DatabasesAtMax(c.Name))
            .ThenBy(c => preferred.FindIndex(p => p.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : int.MaxValue)
            .ThenByDescending(c => c.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => synchronizedAtLoss.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .First();

        var mostAdvanced = DatabasesAtMax(best.Name) == databases.Count;
        var missingSynchronized = synchronizedAtLoss.Where(r => !IsCandidate(r)).ToList();

        if (!mostAdvanced)
            return new PromotionDecision(best.Name, false, "no single survivor holds the most recent log of every database");
        if (requiredSync < 1)
            return new PromotionDecision(best.Name, false,
                "REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT was 0: the primary could commit without any secondary");
        if (synchronizedAtLoss.Count == 0)
            return new PromotionDecision(best.Name, false, "no secondary was synchronized at the last observation of the primary");
        if (missingSynchronized.Count > 0)
            return new PromotionDecision(best.Name, false,
                $"replica(s) synchronized at the last observation are unreachable: {string.Join(", ", missingSynchronized)}");

        return new PromotionDecision(best.Name, true,
            $"it holds the highest hardened LSN of every database and every synchronized replica survived");
    }

    /// <summary>
    /// Keeps, from primary-sourced reports only, which replicas were synchronized and the commit
    /// durability in force. Written only on change, so the status stays stable.
    /// </summary>
    private static void RecordPrimaryView(SqlAvailabilityGroup ag, AgHealthReport health)
    {
        if (health.PrimaryReplica is null || health.DetectedPrimaries is { Count: > 1 })
            return;

        var synchronized = health.ReplicaStates
            .Where(r => !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                        && r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase)
                        && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
            .Where(r =>
            {
                var dbs = health.DatabaseStates
                    .Where(d => d.ReplicaName.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && !IsSeeding(d))
                    .ToList();
                return dbs.Count == 0 ? r.IsFailoverReady : dbs.All(IsSynchronized);
            })
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var status = ag.Status!;
        if (!(status.LastSynchronizedReplicas ?? []).SequenceEqual(synchronized, StringComparer.Ordinal))
            status.LastSynchronizedReplicas = synchronized;
        if (health.RequiredSynchronizedSecondaries is { } required)
            status.LastRequiredSynchronizedSecondaries = required;
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

    /// <summary>
    /// Starts a failover to <paramref name="targetReplica"/>: isolates the previous primary, then
    /// promotes. A reachable previous primary is demoted after the documented pre-checks and
    /// the promotion follows in the same pass; an unreachable one is fenced over several passes
    /// and the promotion happens in <see cref="ContinueFencedFailoverAsync"/>.
    /// </summary>
    private async Task<FailoverOutcome> ExecuteFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, string targetReplica,
        bool allowDataLoss, string reason, CancellationToken ct, long? lagKb = null)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;

        var oldPrimary = health.PrimaryReplica ?? ag.Status?.PrimaryReplica;
        if (oldPrimary != null && oldPrimary.Equals(targetReplica, StringComparison.OrdinalIgnoreCase))
            oldPrimary = null;

        try
        {
            // Open the target connection FIRST: a promotion we cannot even reach must not leave the
            // old primary de-routed or fenced.
            await using var targetConn = await connectionFactory.CreateConnectionAsync(
                Fqdn(ag, targetReplica), SqlEndpoints.ContainerPort, saPassword, "master", ct);

            if (oldPrimary == null)
                return await PromoteAsync(ag, saPassword, health, targetReplica, null, allowDataLoss, reason, lagKb, null, targetConn, ct);

            // De-route the last known primary even when it is absent from the reachable set: a
            // partitioned-but-alive primary kept its writer label and kept accepting writes
            // through the listener. Label patches are k8s API calls — they work
            // regardless of the pod's SQL reachability.
            await agPodManager.SetPodRoleLabelAsync(oldPrimary, ns, name, "resolving", ct);
            await agPodManager.SetReadabilityLabelsAsync(oldPrimary, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

            // 1. Reachable → demote it: the documented clusterless procedure, which leaves the replica
            //    ready to rejoin as a secondary.
            Demotion demotion;
            try
            {
                demotion = await TryDemoteAsync(ag, oldPrimary, targetReplica, targetConn, saPassword, allowDataLoss, ct);
            }
            catch (FailoverPreconditionException ex)
            {
                return await AbortFailoverAsync(ag, saPassword, oldPrimary, targetReplica, ex.Message, ct);
            }

            if (demotion.NotWriting)
            {
                // Only a replica demoted in THIS pass may be re-promoted when the promotion fails: one
                // that already answered as non-primary was never the writer, and FORCE_FAILOVER on it
                // would promote a copy that may be behind.
                await using (demotion.Demoted)
                    return await PromoteAsync(ag, saPassword, health, targetReplica, oldPrimary, allowDataLoss, reason, lagKb, demotion.Demoted, targetConn, ct);
            }

            // 2. Unreachable (or the demotion did not take) → fencing must PROVE it cannot write.
            var strategy = ag.Spec.Failover?.SplitBrainProtection?.FencingStrategy ?? "NetworkIsolation";
            if (!strategy.Equals("NetworkIsolation", StringComparison.OrdinalIgnoreCase))
            {
                await eventPublisher.PublishAsync(ag, "FencingSkipped",
                    $"Fencing strategy '{strategy}' requires external/manual action for {oldPrimary}; the operator will not promote until it is confirmed down",
                    "Warning", ct);
                return await AbortFailoverAsync(ag, saPassword, oldPrimary, targetReplica,
                    $"fencingStrategy={strategy} delegates fencing to an external actor", ct);
            }

            // The pod is recorded up front: a record persisted without it (FenceAsync failing before
            // filling it in) sent every later pass to ContinueFencedFailoverAsync with an empty name.
            var fencing = ag.Status!.Fencing = new AgFencingStatus
            {
                Pod = oldPrimary,
                Target = targetReplica,
                AllowDataLoss = allowDataLoss,
                Reason = reason,
            };
            return await AdvanceFencingAsync(ag, saPassword, health, fencing, oldPrimary, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A fencing that never reached a stage did nothing that a later pass must finish: the
            // failover failed, and the decision is taken again from scratch.
            if (ag.Status?.Fencing is { Stage: "" })
                ag.Status.Fencing = null;
            logger.LogError(ex, "Failover of AG {Ag} to {Target} failed", name, targetReplica);
            await eventPublisher.PublishAsync(ag, "FailoverFailed",
                $"Failover of AG {name} to {targetReplica} failed: {ex.Message}", "Warning", ct);
            return FailoverOutcome.Failed;
        }
    }

    /// <summary>
    /// Drives a fencing started on an earlier pass. The failover it serves is dropped when
    /// another replica became primary meanwhile, or when the previous primary came back as a new
    /// instance — it is then simply the primary again, there is nothing left to fail over.
    /// </summary>
    private async Task<FailoverOutcome> ContinueFencedFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, AgFencingStatus fencing, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var status = ag.Status!;

        // A record that does not name the pod it fences cannot be driven anywhere.
        if (string.IsNullOrEmpty(fencing.Pod))
        {
            status.Fencing = null;
            await eventPublisher.PublishAsync(ag, "FailoverCancelled",
                $"Failover to {fencing.Target ?? "(none)"} cancelled: the fencing record names no pod; the decision is taken again", "Warning", ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        try
        {
            if (health.PrimaryReplica is { } current)
            {
                var pod = await kubernetesClient.GetAsync<k8s.Models.V1Pod>(fencing.Pod, ns, ct);
                var sameInstance = fencing.PodUid is not null && pod?.Metadata.Uid == fencing.PodUid;
                if (!current.Equals(fencing.Pod, StringComparison.OrdinalIgnoreCase) || !sameInstance)
                {
                    status.Fencing = null;
                    await eventPublisher.PublishAsync(ag, "FailoverCancelled",
                        $"Failover to {fencing.Target} cancelled: {current} is the primary again, fencing of {fencing.Pod} stopped", "Warning", ct);
                    return FailoverOutcome.NoAction;
                }
                // Same instance still answering as PRIMARY while its graceful deletion runs: keep going.
            }

            if (fencing.Target is not { } target
                || !health.ReachableReplicas.Contains(target, StringComparer.OrdinalIgnoreCase)
                || IsVersionRegression(ag, target, health))
            {
                status.Fencing = null;
                // Give the previous primary a full window to come back before deciding again.
                status.PrimaryUnreachableCount = 0;
                status.PrimaryUnreachableSince = null;
                await eventPublisher.PublishAsync(ag, "FailoverCancelled",
                    $"Failover cancelled: the promotion target {fencing.Target ?? "(none)"} is no longer eligible; the decision is taken again", "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            return await AdvanceFencingAsync(ag, saPassword, health, fencing, fencing.Pod, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A transient error no longer fails the whole AG pass. While the record is there, the next
            // pass resumes the fencing; once cleared, it was the promotion that failed.
            logger.LogError(ex, "AG {Ag}: failover to {Target} after fencing {Pod} failed", ag.Metadata.Name, fencing.Target, fencing.Pod);
            await eventPublisher.PublishAsync(ag, "FailoverFailed",
                $"Failover of AG {ag.Metadata.Name} to {fencing.Target} failed: {ex.Message}", "Warning", ct);
            return status.Fencing is null ? FailoverOutcome.Failed : FailoverOutcome.FencingInProgress;
        }
    }

    private async Task<FailoverOutcome> AdvanceFencingAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, AgFencingStatus fencing, string oldPrimary, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var status = ag.Status!;
        // splitBrainProtection.fencingTimeoutSeconds bounds the storage detach wait.
        var storageTimeout = TimeSpan.FromSeconds(Math.Max(10, ag.Spec.Failover?.SplitBrainProtection?.FencingTimeoutSeconds ?? 30));

        var result = await replicaFencer.FenceAsync(oldPrimary, ns, fencing, storageTimeout, ct);
        if (result.InProgress)
        {
            await eventPublisher.PublishAsync(ag, "FencingInProgress",
                $"Fencing previous primary {oldPrimary} before promoting {fencing.Target}: {result.Detail}", "Warning", ct);
            return FailoverOutcome.FencingInProgress;
        }

        status.Fencing = null;
        var target = fencing.Target!;
        if (!result.Confirmed)
        {
            await eventPublisher.PublishAsync(ag, "FencingFailed",
                $"Could not fence previous primary {oldPrimary}: {result.Detail}", "Warning", ct);
            return await AbortFailoverAsync(ag, saPassword, oldPrimary, target, result.Detail, ct);
        }

        await eventPublisher.PublishAsync(ag, "ReplicaFenced",
            $"Previous primary {oldPrimary} fenced before failover ({result.Method}: {result.Detail})", "Warning", ct);
        logger.LogWarning("AG {Ag}: previous primary {OldPrimary} isolated ({Method}: {Detail})",
            ag.Metadata.Name, oldPrimary, result.Method, result.Detail);
        return await PromoteAsync(ag, saPassword, health, target, oldPrimary, fencing.AllowDataLoss,
            fencing.Reason ?? "Failover", fencing.AllowDataLoss ? null : 0, null, null, ct);
    }

    /// <summary>
    /// A failover that will not happen: says so, and — gives the previous primary its writer
    /// label back when it still answers as PRIMARY. It was de-routed before the isolation attempt;
    /// leaving it so turned an aborted failover into a write outage.
    /// </summary>
    private async Task<FailoverOutcome> AbortFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, string oldPrimary, string target, string detail, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        await eventPublisher.PublishAsync(ag, "FailoverAborted",
            $"Failover of AG {name} to {target} ABORTED: the previous primary {oldPrimary} could not be " +
            $"demoted nor fenced ({detail}). Promoting now would create two primaries and diverge writes. " +
            "Stop the old primary (or fence its node) and retry.", "Warning", ct);
        logger.LogError("Failover of AG {Ag} to {Target} aborted: {OldPrimary} not isolated — {Detail}", name, target, oldPrimary, detail);

        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                Fqdn(ag, oldPrimary), SqlEndpoints.ContainerPort, saPassword, "master", ct);
            if (string.Equals(await agManager.GetLocalReplicaRoleAsync(conn, name, ct), "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                await agPodManager.SetPodRoleLabelAsync(oldPrimary, ns, name, "primary", ct);
                await agPodManager.SetReadabilityLabelsAsync(oldPrimary, ns, readable: false, readIntentReady: false, listenerReady: "writer", ct);
                await eventPublisher.PublishAsync(ag, "WriterRestored",
                    $"{oldPrimary} still holds the primary role: its writer label is restored", "Warning", ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "AG {Ag}: previous primary {OldPrimary} not reachable after the aborted failover", name, oldPrimary);
        }

        return FailoverOutcome.Failed;
    }

    /// <summary>
    /// Promotes <paramref name="targetReplica"/> and records the failover. When the previous primary
    /// was demoted in this pass (<paramref name="demotedOldPrimary"/>), a failed promotion re-promotes
    /// it: it holds every committed transaction, and leaving the AG without a primary until the
    /// automatic path kicked in was the worst outcome. <paramref name="targetConnection"/> is the
    /// connection the caller already holds to the target, when it has one: a second one only added
    /// latency and a failure point on the RTO path.
    /// </summary>
    private async Task<FailoverOutcome> PromoteAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, string targetReplica, string? oldPrimary,
        bool allowDataLoss, string reason, long? lagKb, Microsoft.Data.SqlClient.SqlConnection? demotedOldPrimary,
        Microsoft.Data.SqlClient.SqlConnection? targetConnection, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;

        await using var ownConnection = targetConnection is null
            ? await connectionFactory.CreateConnectionAsync(Fqdn(ag, targetReplica), SqlEndpoints.ContainerPort, saPassword, "master", ct)
            : null;
        var conn = targetConnection ?? ownConnection!;

        try
        {
            // In a clusterless AG this is the ONLY promotion command — lossless or not depends on
            // the state of the target, which the caller has already gated.
            if (allowDataLoss)
                await agManager.ForceFailoverWithDataLossAsync(conn, name, ct);
            else
                await agManager.FailoverAsync(conn, name, ct);
        }
        catch (Exception ex) when (demotedOldPrimary != null && oldPrimary != null && !ct.IsCancellationRequested)
        {
            await RollBackDemotionAsync(ag, oldPrimary, demotedOldPrimary, $"the promotion of {targetReplica} failed ({ex.Message})", ct);
            return FailoverOutcome.Failed;
        }

        metrics.AgFailover(ns, name);

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

        // The previous primary was taken OFFLINE (RESOLVING): now that the target is primary, it takes
        // the secondary role and rejoins as a secondary (its databases are resumed by the AG database
        // controller). A failure leaves it RESOLVING — not writing — and is said.
        if (demotedOldPrimary != null && oldPrimary != null)
        {
            try
            {
                await agManager.SetSecondaryRoleAsync(demotedOldPrimary, name, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG {Ag}: {OldPrimary} could not take the secondary role after the failover", name, oldPrimary);
                await eventPublisher.PublishAsync(ag, "OldPrimaryStillResolving",
                    $"{oldPrimary} was taken offline for the failover to {targetReplica} but could not take the secondary role: {ex.Message}. " +
                    "Give it the secondary role by hand (SET (ROLE = SECONDARY) on that replica).", "Warning", ct);
            }
        }

        return allowDataLoss ? FailoverOutcome.ForcedFailover : FailoverOutcome.PlannedFailover;
    }

    /// <summary>Re-promotes a primary demoted in this pass when the failover cannot complete.</summary>
    private async Task RollBackDemotionAsync(SqlAvailabilityGroup ag, string oldPrimary,
        Microsoft.Data.SqlClient.SqlConnection demotedOldPrimary, string why, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        try
        {
            await agManager.FailoverAsync(demotedOldPrimary, name, ct);
            await agPodManager.SetPodRoleLabelAsync(oldPrimary, ns, name, "primary", ct);
            await agPodManager.SetReadabilityLabelsAsync(oldPrimary, ns, readable: false, readIntentReady: false, listenerReady: "writer", ct);
            await eventPublisher.PublishAsync(ag, "FailoverRolledBack",
                $"Failover of AG {name} rolled back: {why}. {oldPrimary}, demoted for the failover, was promoted again.", "Warning", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "AG {Ag}: rollback of the demotion of {OldPrimary} failed", name, oldPrimary);
            await eventPublisher.PublishAsync(ag, "FailoverRollbackFailed",
                $"Failover of AG {name} failed ({why}) and {oldPrimary} could not be promoted back ({ex.Message}): the AG has no primary. " +
                $"Set {ForceFailoverToAnnotation}=<replica> to recover.", "Warning", ct);
        }
    }

    private static string Fqdn(SqlAvailabilityGroup ag, string replica)
        => $"{replica}.{ag.Metadata.Name}-headless.{ag.Metadata.NamespaceProperty}.svc.cluster.local";

    /// <summary>A documented precondition of the planned failover does not hold.</summary>
    private sealed class FailoverPreconditionException(string message) : Exception(message);

    /// <summary>
    /// Resolves the resurrected-old-primary split-brain automatically. Applies only
    /// when the detected primaries are EXACTLY the {From, To} pair of the last failover the operator
    /// itself executed AND the operator still believes To is the primary — the one configuration
    /// where the stale side is a matter of record, not of guesswork. From is de-routed, then demoted
    /// with the verified <c>SET (ROLE = SECONDARY)</c>. Returns false (→ conservative drain) when the
    /// shape does not match or the demotion cannot be confirmed. A human doing deliberate manual
    /// surgery is expected to set reconcile-paused, which stops this path with the rest of the
    /// reconcile.
    /// </summary>
    private async Task<bool> TryDemoteResurrectedPrimaryAsync(
        SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var detected = health.DetectedPrimaries!;
        var last = ag.Status?.LastFailover;

        if (last?.From is not { } from || last.To is not { } to)
            return false;
        if (detected.Count != 2
            || !detected.Contains(from, StringComparer.OrdinalIgnoreCase)
            || !detected.Contains(to, StringComparer.OrdinalIgnoreCase)
            || from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return false;
        // Someone failed back (or the record is stale): the record no longer designates the survivor.
        // A pass in split-brain clears the recorded primary — requiring it to equal `to`
        // made the first failed attempt the last one, and the AG stayed drained for good.
        if (ag.Status!.PrimaryReplica is { } recorded && !recorded.Equals(to, StringComparison.OrdinalIgnoreCase))
            return false;

        // De-route the resurrected side first, so no client reaches it while it is being demoted.
        await agPodManager.SetPodRoleLabelAsync(from, ns, name, "resolving", ct);
        await agPodManager.SetReadabilityLabelsAsync(from, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

        try
        {
            var fqdn = $"{from}.{name}-headless.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (!string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                return true; // already resolved between the probe and now

            // OFFLINE first (SET ROLE on an online primary fails with 41104), then the secondary
            // role — `to` is the primary already.
            await agManager.DemoteToSecondaryAsync(conn, name, ct);
            await agManager.SetSecondaryRoleAsync(conn, name, ct);
            role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("AG {Ag}: resurrected old primary {From} still reports PRIMARY after demotion; falling back to the split-brain drain", name, from);
                return false;
            }

            await eventPublisher.PublishAsync(ag, "ResurrectedPrimaryDemoted",
                $"The previous primary {from} came back as PRIMARY after the failover to {to} and was demoted to SECONDARY. " +
                $"Any write it accepted while resurrected is divergent and lost. If its databases do not resynchronize, " +
                $"remove it from availability group {name} and let the operator re-seed it.", "Warning", ct);
            logger.LogWarning("AG {Ag}: demoted resurrected old primary {From}; {To} remains the primary", name, from, to);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AG {Ag}: could not demote resurrected old primary {From}; falling back to the split-brain drain", name, from);
            return false;
        }
    }

    /// <summary>What <see cref="TryDemoteAsync"/> established about the previous primary.
    /// <see cref="NotWriting"/>: it answered without the primary role, so no fencing is needed.
    /// <see cref="Demoted"/>: the connection to it, set only when THIS pass demoted it — the one case
    /// a failed promotion may roll back.</summary>
    private readonly record struct Demotion(bool NotWriting, Microsoft.Data.SqlClient.SqlConnection? Demoted);

    /// <summary>
    /// Demotes a reachable previous primary: taken OFFLINE, it goes RESOLVING and
    /// accepts no more writes. When it cannot be reached, <see cref="Demotion.NotWriting"/> is false —
    /// fencing then takes over. A primary that answers but refuses the demotion is alive
    /// and still the writer; it used to be treated as unreachable and fenced — its pod deleted in the
    /// middle of a planned failover. The failover is aborted instead, and it keeps the writer role.
    ///
    /// For a failover without data loss, the documented clusterless procedure is followed
    /// before demoting: REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT is raised to at least 1 (it may
    /// have been lowered to 0, and the target must then harden every commit up to the demotion), the
    /// target must be SYNCHRONIZED on every database as seen by the primary, and both replicas must
    /// share the same configuration sequence number. A failed precondition aborts the failover.
    /// </summary>
    private async Task<Demotion> TryDemoteAsync(SqlAvailabilityGroup ag, string oldPrimary,
        string target, Microsoft.Data.SqlClient.SqlConnection targetConn, string saPassword, bool allowDataLoss, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        Microsoft.Data.SqlClient.SqlConnection? conn = null;
        var reachable = false;
        try
        {
            conn = await connectionFactory.CreateProbeConnectionAsync(
                Fqdn(ag, oldPrimary), SqlEndpoints.ContainerPort, saPassword, "master", ct);

            var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            reachable = true;
            if (!string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("AG {Ag}: {OldPrimary} does not hold the primary role (role={Role})", name, oldPrimary, role ?? "none");
                return new Demotion(NotWriting: true, Demoted: null);
            }

            if (!allowDataLoss)
                await CheckPlannedFailoverPreconditionsAsync(name, conn, target, targetConn, ct);

            // OFFLINE is synchronous: when it returns, the replica is RESOLVING. Re-read the role once
            // — trust nothing, verify.
            await agManager.DemoteToSecondaryAsync(conn, name, ct);

            role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
            if (!string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            {
                await eventPublisher.PublishAsync(ag, "PrimaryDemoted",
                    $"Previous primary {oldPrimary} taken offline (role {role ?? "none"}) before promoting the new primary", "Warning", ct);
                var demoted = conn;
                conn = null;
                return new Demotion(NotWriting: true, Demoted: demoted);
            }

            throw new FailoverPreconditionException($"{oldPrimary} still reports PRIMARY after being taken offline");
        }
        catch (FailoverPreconditionException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && reachable)
        {
            logger.LogWarning(ex, "AG {Ag}: previous primary {OldPrimary} answers but refused the demotion; aborting the failover", name, oldPrimary);
            throw new FailoverPreconditionException($"{oldPrimary} answers but refused the demotion: {ex.Message}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AG {Ag}: previous primary {OldPrimary} is not reachable for demotion; fencing instead", name, oldPrimary);
            return default;
        }
        finally
        {
            if (conn != null)
                await conn.DisposeAsync();
        }
    }

    private async Task CheckPlannedFailoverPreconditionsAsync(string agName, Microsoft.Data.SqlClient.SqlConnection primaryConn,
        string target, Microsoft.Data.SqlClient.SqlConnection targetConn, CancellationToken ct)
    {
        if (await agManager.GetRequiredSyncSecondariesAsync(primaryConn, agName, ct) is null or 0)
            await agManager.SetRequiredSyncSecondariesAsync(primaryConn, agName, 1, ct);

        var notSynchronized = (await agManager.GetDatabaseStatesAsync(primaryConn, agName, ct))
            .Where(d => d.ReplicaName.Equals(target, StringComparison.OrdinalIgnoreCase) && !IsSeeding(d) && !IsSynchronized(d))
            .Select(d => d.DatabaseName)
            .ToList();
        if (notSynchronized.Count > 0)
            throw new FailoverPreconditionException(
                $"{target} is not SYNCHRONIZED on {string.Join(", ", notSynchronized)}");

        var primarySequence = await agManager.GetAgSequenceNumberAsync(primaryConn, agName, ct);
        var targetSequence = await agManager.GetAgSequenceNumberAsync(targetConn, agName, ct);
        if (primarySequence != targetSequence)
            throw new FailoverPreconditionException(
                $"{target} has not caught up with the availability group configuration (sequence {targetSequence?.ToString() ?? "unknown"} " +
                $"vs {primarySequence?.ToString() ?? "unknown"} on the primary)");
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
        // The rolling update's request is consumed with the user's (persisted with the status).
        if (ag.Status?.RollingUpdate is { FailoverTarget: not null } rollout)
            rollout.FailoverTarget = null;
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
