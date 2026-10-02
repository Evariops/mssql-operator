using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// AG-aware rolling update orchestrator. Drives a StatefulSet in <c>OnDelete</c> mode one
/// pod at a time — async secondaries, then sync secondaries, then a planned failover before the old
/// primary — never exceeding one unavailable pod and never updating while the AG is unhealthy.
///
/// The state machine is almost entirely <em>derived</em> from observed state (the StatefulSet
/// <c>updateRevision</c> vs each pod's <c>controller-revision-hash</c>, plus the health report), so a
/// crash mid-rollout simply resumes from reality. <see cref="AgRollingUpdateStatus"/> only carries
/// the in-flight pod, the pause gate and timestamps for timeout/observability.
///
/// Planned failover is delegated to <see cref="AgFailoverOrchestrator"/> via the failover-to
/// annotation, processed on the next cycle (failover runs before rolling update).
/// </summary>
public interface IAgRollingUpdateOrchestrator
{
    Task<RollingUpdateOutcome> ReconcileAsync(SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, CancellationToken ct);
}

public enum RollingUpdateOutcome
{
    Idle,
    InProgress,
    Completed,
    Stalled,
}

public class AgRollingUpdateOrchestrator(
    ILogger<AgRollingUpdateOrchestrator> logger,
    IKubernetesClient kubernetesClient,
    IAgPodManager agPodManager,
    IEventPublisher eventPublisher,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IFullTextManager fullTextManager,
    OperatorMetrics metrics,
    TimeProvider timeProvider) : IAgRollingUpdateOrchestrator
{
    private const string RevisionLabel = "controller-revision-hash";

    /// <summary>Operator annotation to resume a rollout that was stopped (Failed) after a timeout.</summary>
    public const string RetryAnnotation = "mssql-operator.evariops.com/retry-rolling-update";

    public async Task<RollingUpdateOutcome> ReconcileAsync(SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        ag.Status ??= new SqlAvailabilityGroupStatus();

        var sts = await kubernetesClient.GetAsync<V1StatefulSet>(name, ns, ct);
        var targetRevision = sts?.Status?.UpdateRevision;
        if (sts is null || string.IsNullOrEmpty(targetRevision))
            return RollingUpdateOutcome.Idle; // No STS / revision yet (still initializing).

        var pods = await agPodManager.GetAgPodsAsync(name, ns, name, ct);
        var stalePods = pods
            .Where(p => p.Metadata.Labels is null
                        || !p.Metadata.Labels.TryGetValue(RevisionLabel, out var rev)
                        || rev != targetRevision)
            .Select(p => p.Metadata.Name)
            .ToHashSet();

        var ru = ag.Status.RollingUpdate;
        var wasInProgress = ru is not null && ru.Phase is not ("Idle" or "Failed");

        // Nothing stale → done (clear any in-flight or failed state).
        if (stalePods.Count == 0)
        {
            metrics.SetRollingUpdateInProgress(ag.Metadata.NamespaceProperty, name, false);
            if (wasInProgress)
                await eventPublisher.PublishAsync(ag, "RollingUpdateCompleted",
                    $"AG {name} rolling update to revision {targetRevision} completed", ct: ct);
            if (ru is null || ru.Phase != "Idle")
                ag.Status.RollingUpdate = new AgRollingUpdateStatus { Phase = "Idle", TotalPods = pods.Count, UpdatedPods = pods.Count, TargetRevision = targetRevision };
            return RollingUpdateOutcome.Idle;
        }

        metrics.SetRollingUpdateInProgress(ag.Metadata.NamespaceProperty, name, true);

        // A previously failed rollout stays Failed until an operator explicitly retries it via the
        // retry annotation — it must NOT silently auto-resume on the next cycle.
        if (ru is { Phase: "Failed" })
        {
            if (ag.Metadata.Annotations?.ContainsKey(RetryAnnotation) == true)
            {
                ag.Metadata.Annotations.Remove(RetryAnnotation);
                var refreshed = await kubernetesClient.UpdateAsync(ag, ct);
                ag.Metadata.ResourceVersion = refreshed.Metadata.ResourceVersion;
                ru.Phase = "Planning";
                ru.CurrentPod = null;
                ru.CurrentStepStartedAt = null;
                ru.NextActionAfter = null;
                ru.BlockedSince = null;
                await eventPublisher.PublishAsync(ag, "RollingUpdateRetry", $"Rolling update of AG {name} retried by operator", ct: ct);
            }
            else
            {
                return RollingUpdateOutcome.Stalled; // remain failed, await manual intervention
            }
        }

        ru = ag.Status.RollingUpdate ??= new AgRollingUpdateStatus { StartedAt = now };
        ru.StartedAt ??= now;
        ru.TargetRevision = targetRevision;
        ru.TotalPods = pods.Count;
        ru.UpdatedPods = pods.Count - stalePods.Count;
        if (ru.Phase == "Idle")
            ru.Phase = "Planning";

        // We only act when the AG is healthy enough to lose one more pod. This is what enforces
        // maxUnavailable=1 and pauses the rollout the instant an incident appears (the failover path
        // runs first).
        //
        // Replicas that are still STALE are exempt from the *synchronization health*
        // requirement. After a failover-for-update in a cross-version campaign, the old primary
        // (still on the previous build) cannot redo the log of the new, upgraded primary: it stays
        // NOT_HEALTHY forever. The gate then forbade deleting the very pod whose deletion — and
        // recreation on the new image — was the cure, and no timeout applied in that state, so the
        // rollout hung InProgress indefinitely with the AG running without redundancy. A stale
        // replica must still be CONNECTED (a replica on a dead node does not qualify) — so we never
        // take a second pod down while one is genuinely missing.
        var expected = sts.Spec.Replicas ?? pods.Count;
        var clusterHealthy =
            pods.Count == expected
            && pods.All(p => p.Status?.ContainerStatuses?.All(c => c.Ready) == true)
            && health.PrimaryReplica != null
            && health.ReplicasMissingMembership.Count == 0
            && health.ReplicaStates.Count == expected
            && health.ReplicaStates.All(r =>
                r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
            && health.ReplicaStates
                .Where(r => !stalePods.Contains(r.Name))
                .All(r => r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));

        // Waiting on the pod we last deleted?
        if (ru.CurrentPod != null)
        {
            var done = !stalePods.Contains(ru.CurrentPod) && clusterHealthy;
            if (done)
            {
                await eventPublisher.PublishAsync(ag, "RollingUpdatePodUpdated",
                    $"Replica {ru.CurrentPod} updated to revision {targetRevision}", ct: ct);
                ru.CurrentPod = null;
                ru.Step = "Pausing";
                ru.NextActionAfter = now + TimeSpan.FromSeconds(PauseSeconds(ag));
                return RollingUpdateOutcome.InProgress;
            }

            // Still rebuilding/resyncing — enforce the per-pod timeout.
            if (ru.CurrentStepStartedAt is { } started
                && now - started > TimeSpan.FromSeconds(TimeoutSeconds(ag)))
            {
                ru.Phase = "Failed";
                ru.Step = "WaitingHealthy";
                await eventPublisher.PublishAsync(ag, "RollingUpdateStalled",
                    $"Rolling update of {ru.CurrentPod} exceeded {TimeoutSeconds(ag)}s without becoming healthy; stopping", "Warning", ct);
                return RollingUpdateOutcome.Stalled;
            }
            ru.Step = "WaitingHealthy";
            return RollingUpdateOutcome.InProgress;
        }

        // In-flight failover initiated to update the primary: wait for the new primary, bounded
        // by the timeout, and never re-issue it. Checked before the health gate so the timeout fires
        // even if the AG is unhealthy (e.g. a failover that never lands). Success is detected when the
        // primary is no longer stale — the old primary then becomes a stale secondary, handled below.
        if (ru.Phase == "FailingOverForUpdate")
        {
            var primaryNow = health.PrimaryReplica;
            if (primaryNow != null && !stalePods.Contains(primaryNow))
            {
                ru.Phase = "Planning"; // failover landed on an up-to-date replica; resume normal flow
                ru.Step = null;
            }
            else if (ru.CurrentStepStartedAt is { } s && now - s > TimeSpan.FromSeconds(TimeoutSeconds(ag)))
            {
                ru.Phase = "Failed";
                await eventPublisher.PublishAsync(ag, "RollingUpdateStalled",
                    $"Failover to update the primary did not complete within {TimeoutSeconds(ag)}s; stopping. " +
                    $"Resolve, then annotate {RetryAnnotation} to resume.", "Warning", ct);
                return RollingUpdateOutcome.Stalled;
            }
            else
            {
                ru.Step = "WaitingFailover";
                return RollingUpdateOutcome.InProgress;
            }
        }

        // No pod in flight. Don't start the next one while the AG is unhealthy or within the pause.
        if (!clusterHealthy)
        {
            // A rollout that can never make progress must SAY so instead of sitting
            // InProgress forever. Nothing else times this state out (the per-pod timeout requires a
            // CurrentPod, which is null here).
            ru.BlockedSince ??= now;
            ru.Step = "WaitingHealthy";
            if (now - ru.BlockedSince.Value > TimeSpan.FromSeconds(StallTimeoutSeconds(ag)))
            {
                ru.Phase = "Failed";
                await eventPublisher.PublishAsync(ag, "RollingUpdateStalled",
                    $"Rolling update of AG {name} has been blocked by AG health for {StallTimeoutSeconds(ag)}s " +
                    $"({string.Join("; ", health.Warnings.Take(3))}); stopping. Resolve, then annotate {RetryAnnotation} to resume.",
                    "Warning", ct);
                return RollingUpdateOutcome.Stalled;
            }
            return RollingUpdateOutcome.InProgress;
        }

        if (ru.NextActionAfter is { } gate && now < gate)
        {
            ru.Step = "Pausing";
            return RollingUpdateOutcome.InProgress;
        }

        // Never take a pod down while it is running a full-text population.
        // Interrupting a crawl is what makes the NEXT boot pathological — the engine automatically
        // resumes the interrupted population at startup, on top of a cold buffer pool, and the pod
        // takes minutes to become genuinely usable.
        //
        // This gate is bounded HERE, by the same BlockedSince clock as the health gate
        // above. The previous code reset BlockedSince to null on every healthy pass right before
        // re-arming it (so it never accumulated), and never checked the stall timeout in this branch
        // at all — a population that never ended blocked the rollout forever while the comment
        // claimed otherwise. BlockedSince is only cleared once BOTH gates are passed.
        if (await IsFullTextBusyAsync(ag, health, saPassword, ct) is { } busy)
        {
            ru.BlockedSince ??= now;
            ru.Step = "WaitingFullText";
            if (now - ru.BlockedSince.Value > TimeSpan.FromSeconds(StallTimeoutSeconds(ag)))
            {
                ru.Phase = "Failed";
                await eventPublisher.PublishAsync(ag, "RollingUpdateStalled",
                    $"Rolling update of AG {name} has been waiting on full-text populations for more than " +
                    $"{StallTimeoutSeconds(ag)}s ({busy}); stopping. Throttle or stop the population, then annotate " +
                    $"{RetryAnnotation} to resume.", "Warning", ct);
                return RollingUpdateOutcome.Stalled;
            }
            await eventPublisher.PublishAsync(ag, "RollingUpdateWaitingFullText",
                $"Deferring the next pod update: {busy}. Interrupting a full-text population makes the next " +
                "startup resume it under a cold cache, which is exactly the slow restart this gate prevents. " +
                $"Waiting since {(int)(now - ru.BlockedSince.Value).TotalSeconds}s (stall timeout {StallTimeoutSeconds(ag)}s).",
                ct: ct);
            return RollingUpdateOutcome.InProgress;
        }
        ru.BlockedSince = null;

        // Choose the next stale pod: async secondaries first, then sync secondaries, highest ordinal
        // first; the primary is handled last via a planned failover.
        var primary = health.PrimaryReplica!;
        var staleSecondaries = health.ReplicaStates
            .Where(r => stalePods.Contains(r.Name) && !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(r => Ordinal(r.Name))
            .ToList();

        if (staleSecondaries.Count > 0)
        {
            var next = staleSecondaries[0];
            ru.Phase = "UpdatingSecondary";
            ru.CurrentPod = next.Name;
            ru.CurrentStepStartedAt = now;
            ru.Step = "DeletingPod";
            logger.LogInformation("Rolling update: deleting secondary {Pod} (target revision {Rev})", next.Name, targetRevision);

            // A synchronous secondary is part of the commit quorum. Deleting it first
            // and only lowering REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT on the next health pass
            // froze every commit on the primary for a full cycle. Lower it BEFORE the pod goes away
            // (the health pass restores it when the replica resynchronizes).
            if (next.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase))
                await LowerRequiredSyncBeforeRemovalAsync(ag, health, saPassword, next.Name, ct);

            await DeletePodAsync(next.Name, ns, ct);
            return RollingUpdateOutcome.InProgress;
        }

        // Only the primary is stale.
        if (stalePods.Contains(primary))
        {
            if (FailoverBeforePrimaryUpdate(ag))
            {
                // Pick an already-updated, synchronized, failover-ready secondary to promote.
                var target = health.ReplicaStates.FirstOrDefault(r =>
                    !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    && !stalePods.Contains(r.Name)
                    && r.IsFailoverReady
                    && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));

                if (target is null)
                {
                    ru.Phase = "Failed";
                    await eventPublisher.PublishAsync(ag, "RollingUpdateStalled",
                        $"Cannot update primary {primary}: no updated, synchronized secondary to fail over to", "Warning", ct);
                    return RollingUpdateOutcome.Stalled;
                }

                ru.Phase = "FailingOverForUpdate";
                ru.Step = "Failover";
                ru.CurrentStepStartedAt = now; // starts the failover timeout clock
                ru.FailoverTarget = target.Name;
                await eventPublisher.PublishAsync(ag, "RollingUpdateFailover",
                    $"Failing over from {primary} to updated secondary {target.Name} before updating the primary", ct: ct);
                return RollingUpdateOutcome.InProgress;
            }

            ru.Phase = "UpdatingPrimary";
            ru.CurrentPod = primary;
            ru.CurrentStepStartedAt = now;
            ru.Step = "DeletingPod";
            await DeletePodAsync(primary, ns, ct);
            return RollingUpdateOutcome.InProgress;
        }

        return RollingUpdateOutcome.InProgress;
    }

    /// <summary>Describes an in-flight full-text population on the primary, or null when there is
    /// none (or full-text is not installed).</summary>
    private async Task<string?> IsFullTextBusyAsync(SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, CancellationToken ct)
    {
        if (health.PrimaryReplica is not { } primary)
            return null;
        // Populations only run on the primary: secondaries receive the index through redo.
        var fqdn = $"{primary}.{ag.Metadata.Name}-headless.{ag.Metadata.NamespaceProperty}.svc.cluster.local";
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);
            var status = await fullTextManager.GetStatusAsync(conn, ct);
            if (!status.HasActivePopulations)
                return null;
            var first = status.ActivePopulations[0];
            return $"{status.ActivePopulations.Count} full-text population(s) running on {primary} " +
                   $"(e.g. {first.PopulationType} on database {first.DatabaseName}, status {first.Status})";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check full-text populations on {Primary}", primary);
            return null;
        }
    }

    /// <summary>
    /// Lowers REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT to what will still be available once the
    /// given synchronous secondary is deleted. Best-effort: if the primary cannot be
    /// reached, the reactive adjustment still repairs it on the next health pass.
    /// </summary>
    private async Task LowerRequiredSyncBeforeRemovalAsync(
        SqlAvailabilityGroup ag, AgHealthReport health, string saPassword, string removedReplica, CancellationToken ct)
    {
        var name = ag.Metadata.Name;
        var primary = health.PrimaryReplica;
        if (primary is null) return;

        var remainingSyncSecondaries = health.ReplicaStates.Count(r =>
            !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase)
            && !r.Name.Equals(removedReplica, StringComparison.OrdinalIgnoreCase)
            && r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase)
            && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
            && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));

        // Honour the durability floor here too. Going below it "for the duration of one
        // pod update" is exactly the window the floor exists to close — a commit acked with zero
        // synchronous replicas is lost by the very failover this rollout may trigger. If the floor
        // cannot be met without the removed replica, commits will block while it restarts: that is
        // the trade the user configured (AdjustRequiredSyncAsync applies the same rule reactively).
        var configured = ag.Spec.Replicas.RequiredSynchronizedSecondariesToCommit;
        var floor = Math.Max(0, Math.Min(ag.Spec.Replicas.RequiredSyncFloor, configured));
        var desired = Math.Max(floor, Math.Min(configured, remainingSyncSecondaries));

        if (floor > remainingSyncSecondaries)
            await eventPublisher.PublishAsync(ag, "DurabilityFloorHoldingWrites",
                $"Updating synchronous secondary {removedReplica} will leave only {remainingSyncSecondaries} synchronous " +
                $"secondary(ies) while the durability floor is {floor}: commits will BLOCK until it resynchronizes " +
                "(spec.replicas.requiredSyncFloor).", "Warning", ct);

        try
        {
            var fqdn = $"{primary}.{name}-headless.{ag.Metadata.NamespaceProperty}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                fqdn, SqlEndpoints.ContainerPort, saPassword, "master", ct);

            var current = await agManager.GetRequiredSyncSecondariesAsync(conn, name, ct);
            if (current == desired) return;

            await agManager.SetRequiredSyncSecondariesAsync(conn, name, desired, ct);
            await eventPublisher.PublishAsync(ag, "RequiredSyncLowered",
                $"Lowered REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT to {desired} before taking synchronous " +
                $"secondary {removedReplica} down for the rolling update (restored when it resynchronizes)", ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not pre-lower REQUIRED_SYNC before deleting {Replica}; commits may briefly block", removedReplica);
        }
    }

    private async Task DeletePodAsync(string podName, string ns, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod != null)
            await kubernetesClient.DeleteAsync(pod, ct);
    }

    private static int Ordinal(string podName)
    {
        var dash = podName.LastIndexOf('-');
        return dash >= 0 && int.TryParse(podName[(dash + 1)..], out var n) ? n : 0;
    }

    private static RollingUpdateSpec Rolling(SqlAvailabilityGroup ag) =>
        ag.Spec.UpdateStrategy?.RollingUpdate ?? new RollingUpdateSpec();

    private static int PauseSeconds(SqlAvailabilityGroup ag) => Rolling(ag).PauseBetweenPodsSeconds;
    private static int TimeoutSeconds(SqlAvailabilityGroup ag) => Rolling(ag).HealthCheckTimeoutSeconds;
    private static bool FailoverBeforePrimaryUpdate(SqlAvailabilityGroup ag) => Rolling(ag).FailoverBeforePrimaryUpdate;

    /// <summary>How long the rollout may stay blocked by AG health (or a full-text population)
    /// before it is declared Failed instead of hanging silently.</summary>
    private static int StallTimeoutSeconds(SqlAvailabilityGroup ag) => Math.Max(600, Rolling(ag).HealthCheckTimeoutSeconds * 10);
}
