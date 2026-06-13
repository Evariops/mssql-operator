using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Drives Availability Group failover for <c>CLUSTER_TYPE = NONE</c> +
/// <c>FAILOVER_MODE = MANUAL</c> groups, where SQL Server performs no automatic promotion and the
/// operator must do it. Handles three triggers:
/// <list type="bullet">
/// <item>manual planned failover via the <c>mssql-operator.evariops.com/failover-to</c> annotation;</item>
/// <item>manual forced (data-loss) failover via <c>mssql-operator.evariops.com/force-failover-to</c>;</item>
/// <item>automatic promotion when no replica reports PRIMARY and a synchronized, failover-ready
/// secondary exists (or a forced failover is explicitly allowed).</item>
/// </list>
/// Before a forced failover it fences the previous primary to avoid split-brain.
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
    IEventPublisher eventPublisher,
    OperatorMetrics metrics) : IAgFailoverOrchestrator
{
    public const string FailoverToAnnotation = "mssql-operator.evariops.com/failover-to";
    public const string ForceFailoverToAnnotation = "mssql-operator.evariops.com/force-failover-to";
    public const string AllowDataLossAnnotation = "mssql-operator.evariops.com/allow-data-loss";

    public async Task<FailoverOutcome> ReconcileFailoverAsync(SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var failoverSpec = ag.Spec.Failover ?? new AgFailoverSpec();

        var annotations = ag.Metadata.Annotations ?? new Dictionary<string, string>();
        annotations.TryGetValue(FailoverToAnnotation, out var plannedTarget);
        annotations.TryGetValue(ForceFailoverToAnnotation, out var forcedTarget);
        var annotationAllowsDataLoss = annotations.TryGetValue(AllowDataLossAnnotation, out var dl)
                                       && bool.TryParse(dl, out var b) && b;

        // During a detected split-brain no consumer may act on an arbitrary
        // "primary" — the only allowed actions are draining the writer path.
        if (health.DetectedPrimaries is { Count: > 1 })
        {
            await eventPublisher.PublishAsync(ag, "SplitBrainDetected",
                $"Multiple primaries detected ({string.Join(", ", health.DetectedPrimaries)}); refusing any failover action until resolved", "Warning", ct);
            await MarkAllResolvingAsync(ag, health, ct);
            return FailoverOutcome.AwaitingManualIntervention;
        }

        // 1. Explicit forced failover requested by an operator/human.
        if (!string.IsNullOrWhiteSpace(forcedTarget))
        {
            // Consume the annotation BEFORE executing — replaying a data-loss
            // failover because the post-execution clear failed is the failure mode to kill.
            if (!await ClearFailoverAnnotationsAsync(ag, ct))
            {
                await eventPublisher.PublishAsync(ag, "FailoverDeferred",
                    "Could not clear failover annotations; deferring the forced failover to the next cycle", "Warning", ct);
                return FailoverOutcome.NoAction;
            }
            return await ExecuteFailoverAsync(ag, saPassword, health, forcedTarget!, forced: true, ct);
        }

        // 2. Explicit planned failover requested.
        if (!string.IsNullOrWhiteSpace(plannedTarget))
        {
            if (!await ClearFailoverAnnotationsAsync(ag, ct))
            {
                await eventPublisher.PublishAsync(ag, "FailoverDeferred",
                    "Could not clear failover annotations; deferring the planned failover to the next cycle", "Warning", ct);
                return FailoverOutcome.NoAction;
            }

            var target = health.ReplicaStates.FirstOrDefault(r =>
                r.Name.Equals(plannedTarget, StringComparison.OrdinalIgnoreCase));
            if (target is { IsFailoverReady: false } && !annotationAllowsDataLoss)
            {
                await eventPublisher.PublishAsync(ag, "FailoverRejected",
                    $"Planned failover to {plannedTarget} rejected: replica is not failover-ready (not synchronized). " +
                    $"Set {AllowDataLossAnnotation}=true and use {ForceFailoverToAnnotation} to force.", "Warning", ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            return await ExecuteFailoverAsync(ag, saPassword, health, plannedTarget!, forced: annotationAllowsDataLoss, ct);
        }

        // 3. No primary reachable → automatic failover, if the operator is allowed to manage it.
        // A single missed pass is a monitoring blip — act only after N consecutive
        // failures (persisted in status, reset on the first healthy pass).
        ag.Status ??= new SqlAvailabilityGroupStatus();
        if (health.PrimaryReplica == null && failoverSpec.OperatorManaged)
        {
            var threshold = Math.Max(1, failoverSpec.PrimaryUnreachableThreshold);
            if (ag.Status.PrimaryUnreachableCount < threshold)
                ag.Status.PrimaryUnreachableCount++;

            if (ag.Status.PrimaryUnreachableCount < threshold)
            {
                await eventPublisher.PublishAsync(ag, "PrimaryUnreachable",
                    $"No primary reachable ({ag.Status.PrimaryUnreachableCount}/{threshold} consecutive passes); deferring failover action", "Warning", ct);
                return FailoverOutcome.NoAction;
            }

            return await AutoFailoverAsync(ag, saPassword, health, failoverSpec, ct);
        }

        if (health.PrimaryReplica != null && ag.Status.PrimaryUnreachableCount > 0)
            ag.Status.PrimaryUnreachableCount = 0;

        return FailoverOutcome.NoAction;
    }

    private async Task<FailoverOutcome> AutoFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, AgFailoverSpec failoverSpec, CancellationToken ct)
    {
        var name = ag.Metadata.Name;

        // Close the write window immediately: with no confirmed primary, no pod may keep the
        // writer label. The R/W listener selector requires listener-ready=writer,
        // so this drains it until a new primary is confirmed.
        await MarkAllResolvingAsync(ag, health, ct);

        // Pick a synchronized, failover-ready secondary — a planned (no data loss) failover target.
        var candidate = SelectTarget(health, failoverSpec, requireFailoverReady: true);
        if (candidate != null)
        {
            await eventPublisher.PublishAsync(ag, "AutoFailoverStarted",
                $"No primary detected; promoting failover-ready secondary {candidate} (no data loss)", "Warning", ct);
            return await ExecuteFailoverAsync(ag, saPassword, health, candidate, forced: false, ct);
        }

        // No synchronized target. Only a data-loss failover can recover — and only if explicitly allowed.
        if (failoverSpec.AllowAutomaticDataLossFailover)
        {
            var forcedCandidate = SelectTarget(health, failoverSpec, requireFailoverReady: false);
            if (forcedCandidate != null)
            {
                await eventPublisher.PublishAsync(ag, "AutoForceFailoverStarted",
                    $"No synchronized secondary; forcing failover to {forcedCandidate} with possible data loss " +
                    "(allowAutomaticDataLossFailover=true)", "Warning", ct);
                return await ExecuteFailoverAsync(ag, saPassword, health, forcedCandidate, forced: true, ct);
            }
        }

        await eventPublisher.PublishAsync(ag, "FailoverNeedsIntervention",
            $"AG {name} has no primary and no synchronized failover-ready secondary. " +
            $"Manual intervention required: set {ForceFailoverToAnnotation}=<replica> and {AllowDataLossAnnotation}=true to force.",
            "Warning", ct);
        return FailoverOutcome.AwaitingManualIntervention;
    }

    private string? SelectTarget(AgHealthReport health, AgFailoverSpec failoverSpec, bool requireFailoverReady)
    {
        bool Eligible(AgReplicaStateInfo r) =>
            !r.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
            && (!requireFailoverReady || (r.IsFailoverReady
                && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)));

        var eligible = health.ReplicaStates.Where(Eligible).ToList();
        if (eligible.Count == 0)
            return null;

        // Honour preferred-target ordinals first.
        if (failoverSpec.PreferredTargets is { Count: > 0 })
        {
            foreach (var pref in failoverSpec.PreferredTargets)
            {
                var match = eligible.FirstOrDefault(r => r.Name.EndsWith($"-{pref.Ordinal}", StringComparison.Ordinal));
                if (match != null) return match.Name;
            }
        }

        if (requireFailoverReady)
            return eligible.OrderBy(r => r.Name).First().Name; // all are synchronized; lowest ordinal for determinism

        // A forced promotion accepts data loss — minimize it. Synchronous-commit
        // secondaries first, then the smallest replication queues (freshest copy).
        var lagByReplica = health.DatabaseStates
            .GroupBy(d => d.ReplicaName)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.LogSendQueueSize + d.RedoQueueSize));

        return eligible
            .OrderByDescending(r => r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => lagByReplica.GetValueOrDefault(r.Name, long.MaxValue))
            .ThenBy(r => r.Name)
            .First().Name;
    }

    private async Task<FailoverOutcome> ExecuteFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, string targetReplica, bool forced, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var targetFqdn = $"{targetReplica}.{headlessService}.{ns}.svc.cluster.local";

        // On EVERY failover, de-route the last known primary even when it is absent
        // from the reachable set — a partitioned-but-alive primary kept its writer label and
        // continued accepting writes through the listener (label patches are k8s API calls,
        // they work regardless of the pod's SQL reachability).
        var oldPrimary = health.PrimaryReplica ?? ag.Status?.PrimaryReplica;
        if (oldPrimary != null && !oldPrimary.Equals(targetReplica, StringComparison.OrdinalIgnoreCase))
        {
            await agPodManager.SetPodRoleLabelAsync(oldPrimary, ns, name, "resolving", ct);
            await agPodManager.SetReadabilityLabelsAsync(oldPrimary, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

            // A forced (data-loss) promotion additionally REQUIRES confirmed fencing.
            if (forced)
            {
                var fenced = await FenceReplicaAsync(ag, oldPrimary, failoverSpec: ag.Spec.Failover, ct);
                if (!fenced)
                {
                    await eventPublisher.PublishAsync(ag, "FencingFailed",
                        $"Forced failover of AG {name} to {targetReplica} aborted: previous primary {oldPrimary} could not be confirmed fenced", "Warning", ct);
                    logger.LogError("Forced failover of AG {Ag} aborted: fencing of {OldPrimary} not confirmed", name, oldPrimary);
                    return FailoverOutcome.Failed;
                }
            }
        }

        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(targetFqdn, 1433, saPassword, "master", ct);

            if (forced)
                await agManager.ForceFailoverWithDataLossAsync(conn, name, ct);
            else
                await agManager.FailoverAsync(conn, name, ct);

            metrics.AgFailover(name);

            // ALTER ... FAILOVER returns before the role flip is observable. Confirm
            // role_desc='PRIMARY' on the target before promoting labels and status — otherwise
            // a failed post-command transition leaves a lying writer label and status.
            var promoted = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var role = await agManager.GetLocalReplicaRoleAsync(conn, name, ct);
                if (string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase))
                {
                    promoted = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            if (!promoted)
            {
                await eventPublisher.PublishAsync(ag, "FailoverUnconfirmed",
                    $"Failover command issued but {targetReplica} did not report PRIMARY within 30s; labels/status left untouched", "Warning", ct);
                return FailoverOutcome.Failed;
            }

            // Promote labels immediately so the listener follows the new primary without waiting
            // for the next health cycle.
            await agPodManager.SetPodRoleLabelAsync(targetReplica, ns, name, "primary", ct);
            await agPodManager.SetReadabilityLabelsAsync(targetReplica, ns, readable: false, readIntentReady: false, listenerReady: "writer", ct);

            ag.Status ??= new SqlAvailabilityGroupStatus();
            ag.Status.LastFailover = new AgLastFailoverStatus
            {
                Timestamp = DateTime.UtcNow,
                From = oldPrimary,
                To = targetReplica,
                Reason = forced ? "ForcedFailover" : "PlannedFailover",
                DataLoss = forced,
            };
            ag.Status.PrimaryReplica = targetReplica;

            await eventPublisher.PublishAsync(ag, forced ? "ForcedFailoverCompleted" : "FailoverCompleted",
                $"AG {name} {(forced ? "force-" : "")}failed over to {targetReplica}", ct: ct);
            logger.LogWarning("AG {Ag} {Kind}failover to {Target} completed", name, forced ? "force-" : "", targetReplica);

            return forced ? FailoverOutcome.ForcedFailover : FailoverOutcome.PlannedFailover;
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
    /// Isolate a replica so it cannot serve traffic. Strategy depends on split-brain config:
    /// for <c>NetworkIsolation</c> we delete the pod (StatefulSet will recreate it; on restart it
    /// rejoins as a secondary after the forced failover), which is effective STONITH for a
    /// single-pod-per-ordinal SQL Server. Labels are cleared first so it drops out of both listeners.
    /// </summary>
    /// <summary>Returns true only when the replica is CONFIRMED fenced. Label-only
    /// strategies are never confirmed for a data-loss failover, and failures propagate to the
    /// caller instead of being swallowed — fencing is not best-effort anymore.</summary>
    private async Task<bool> FenceReplicaAsync(SqlAvailabilityGroup ag, string replica, AgFailoverSpec? failoverSpec, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var name = ag.Metadata.Name;
        var strategy = failoverSpec?.SplitBrainProtection?.FencingStrategy ?? "NetworkIsolation";

        await agPodManager.SetPodRoleLabelAsync(replica, ns, name, "resolving", ct);
        await agPodManager.SetReadabilityLabelsAsync(replica, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

        if (strategy.Equals("NetworkIsolation", StringComparison.OrdinalIgnoreCase))
        {
            var confirmed = await agPodManager.FenceByDeletePodAsync(replica, ns, ct);
            if (confirmed)
                await eventPublisher.PublishAsync(ag, "ReplicaFenced",
                    $"Fenced previous primary {replica} before forced failover (strategy={strategy})", "Warning", ct);
            return confirmed;
        }

        // Relabel-only strategies de-route the listener but cannot stop direct connections:
        // not sufficient for a promotion that accepts data loss.
        await eventPublisher.PublishAsync(ag, "FencingSkipped",
            $"Fencing strategy '{strategy}' requires external/manual action for {replica}; relabelled to resolving only", "Warning", ct);
        return false;
    }

    /// <summary>
    /// Drain the <em>write</em> path while there is no confirmed primary: every reachable pod loses
    /// <c>listener-ready=writer</c>, so the R/W listener (selector requires it) has no backing pod.
    /// Crucially this does NOT take healthy secondaries out of the read path — they stay
    /// <c>ag-role=secondary</c> with their readability labels, so read traffic keeps flowing during
    /// the failover (their data is consistent up to the sync point). Only pods still reporting
    /// <c>PRIMARY</c> (the stale/divergent old primary, or a split-brain) are forced to
    /// <c>resolving</c> and fully de-routed.
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
    /// failed — the caller must NOT execute the failover (the previous swallow allowed
    /// a data-loss failover to replay on every cycle).</summary>
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
