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
/// <item>manual planned failover via the <c>mssql.operator/failover-to</c> annotation;</item>
/// <item>manual forced (data-loss) failover via <c>mssql.operator/force-failover-to</c>;</item>
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
    public const string FailoverToAnnotation = "mssql.operator/failover-to";
    public const string ForceFailoverToAnnotation = "mssql.operator/force-failover-to";
    public const string AllowDataLossAnnotation = "mssql.operator/allow-data-loss";

    public async Task<FailoverOutcome> ReconcileFailoverAsync(SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var failoverSpec = ag.Spec.Failover ?? new AgFailoverSpec();

        var annotations = ag.Metadata.Annotations ?? new Dictionary<string, string>();
        annotations.TryGetValue(FailoverToAnnotation, out var plannedTarget);
        annotations.TryGetValue(ForceFailoverToAnnotation, out var forcedTarget);
        var annotationAllowsDataLoss = annotations.TryGetValue(AllowDataLossAnnotation, out var dl)
                                       && bool.TryParse(dl, out var b) && b;

        // 1. Explicit forced failover requested by an operator/human.
        if (!string.IsNullOrWhiteSpace(forcedTarget))
        {
            var outcome = await ExecuteFailoverAsync(ag, saPassword, health, forcedTarget!, forced: true, ct);
            await ClearFailoverAnnotationsAsync(ag, ct);
            return outcome;
        }

        // 2. Explicit planned failover requested.
        if (!string.IsNullOrWhiteSpace(plannedTarget))
        {
            var target = health.ReplicaStates.FirstOrDefault(r =>
                r.Name.Equals(plannedTarget, StringComparison.OrdinalIgnoreCase));
            if (target is { IsFailoverReady: false } && !annotationAllowsDataLoss)
            {
                await eventPublisher.PublishAsync(ag, "FailoverRejected",
                    $"Planned failover to {plannedTarget} rejected: replica is not failover-ready (not synchronized). " +
                    $"Set {AllowDataLossAnnotation}=true and use {ForceFailoverToAnnotation} to force.", "Warning", ct);
                await ClearFailoverAnnotationsAsync(ag, ct);
                return FailoverOutcome.AwaitingManualIntervention;
            }

            var outcome = await ExecuteFailoverAsync(ag, saPassword, health, plannedTarget!, forced: annotationAllowsDataLoss, ct);
            await ClearFailoverAnnotationsAsync(ag, ct);
            return outcome;
        }

        // 3. No primary reachable → automatic failover, if the operator is allowed to manage it.
        if (health.PrimaryReplica == null && failoverSpec.OperatorManaged)
            return await AutoFailoverAsync(ag, saPassword, health, failoverSpec, ct);

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

        var eligible = health.ReplicaStates.Where(Eligible).Select(r => r.Name).ToList();
        if (eligible.Count == 0)
            return null;

        // Honour preferred-target ordinals first.
        if (failoverSpec.PreferredTargets is { Count: > 0 })
        {
            foreach (var pref in failoverSpec.PreferredTargets)
            {
                var match = eligible.FirstOrDefault(n => n.EndsWith($"-{pref.Ordinal}", StringComparison.Ordinal));
                if (match != null) return match;
            }
        }

        // Otherwise lowest ordinal for determinism.
        return eligible.OrderBy(n => n).First();
    }

    private async Task<FailoverOutcome> ExecuteFailoverAsync(
        SqlAvailabilityGroup ag, string saPassword, AgHealthReport health, string targetReplica, bool forced, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var headlessService = $"{name}-headless";
        var targetFqdn = $"{targetReplica}.{headlessService}.{ns}.svc.cluster.local";

        // Fence the previous primary before a forced (data-loss) promotion so it cannot keep
        // accepting writes and diverge. Skipped for planned failovers, where SQL Server
        // guarantees the target is synchronized and the old primary steps down cleanly.
        var oldPrimary = health.PrimaryReplica ?? ag.Status?.PrimaryReplica;
        if (forced && oldPrimary != null && !oldPrimary.Equals(targetReplica, StringComparison.OrdinalIgnoreCase))
            await FenceReplicaAsync(ag, oldPrimary, failoverSpec: ag.Spec.Failover, ct);

        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(targetFqdn, 1433, saPassword, "master", ct);

            if (forced)
                await agManager.ForceFailoverWithDataLossAsync(conn, name, ct);
            else
                await agManager.FailoverAsync(conn, name, ct);

            metrics.AgFailover(name);

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
    private async Task FenceReplicaAsync(SqlAvailabilityGroup ag, string replica, AgFailoverSpec? failoverSpec, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var name = ag.Metadata.Name;
        var strategy = failoverSpec?.SplitBrainProtection?.FencingStrategy ?? "NetworkIsolation";

        try
        {
            await agPodManager.SetPodRoleLabelAsync(replica, ns, name, "resolving", ct);
            await agPodManager.SetReadabilityLabelsAsync(replica, ns, readable: false, readIntentReady: false, listenerReady: "none", ct);

            if (strategy.Equals("NetworkIsolation", StringComparison.OrdinalIgnoreCase))
            {
                await agPodManager.FenceByDeletePodAsync(replica, ns, ct);
                await eventPublisher.PublishAsync(ag, "ReplicaFenced",
                    $"Fenced previous primary {replica} before forced failover (strategy={strategy})", "Warning", ct);
            }
            else
            {
                await eventPublisher.PublishAsync(ag, "FencingSkipped",
                    $"Fencing strategy '{strategy}' requires external/manual action for {replica}; relabelled to resolving only", "Warning", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort fencing of {Replica} failed", replica);
        }
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

    private async Task ClearFailoverAnnotationsAsync(SqlAvailabilityGroup ag, CancellationToken ct)
    {
        if (ag.Metadata.Annotations == null) return;
        var changed = ag.Metadata.Annotations.Remove(FailoverToAnnotation)
                      | ag.Metadata.Annotations.Remove(ForceFailoverToAnnotation)
                      | ag.Metadata.Annotations.Remove(AllowDataLossAnnotation);
        if (!changed) return;
        try
        {
            var updated = await kubernetesClient.UpdateAsync(ag, ct);
            // Keep resourceVersion fresh so a subsequent status update doesn't 409.
            ag.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear failover annotations on AG {Ag}", ag.Metadata.Name);
        }
    }
}
