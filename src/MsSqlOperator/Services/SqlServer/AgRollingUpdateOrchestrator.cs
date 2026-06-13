using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
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
/// REQUIRED_SYNC is not managed here: reactive adjustment lowers it when a sync secondary
/// drops (i.e. when we delete it) and restores it on resync, which is exactly the guarantee needed.
/// Planned failover is delegated to <see cref="AgFailoverOrchestrator"/> via the failover-to
/// annotation, processed on the next cycle (failover runs before rolling update).
/// </summary>
public interface IAgRollingUpdateOrchestrator
{
    Task<RollingUpdateOutcome> ReconcileAsync(SqlAvailabilityGroup ag, AgHealthReport health, CancellationToken ct);
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
    OperatorMetrics metrics,
    TimeProvider timeProvider) : IAgRollingUpdateOrchestrator
{
    private const string RevisionLabel = "controller-revision-hash";

    /// <summary>Operator annotation to resume a rollout that was stopped (Failed) after a timeout.</summary>
    public const string RetryAnnotation = "mssql-operator.evariops.com/retry-rolling-update";

    public async Task<RollingUpdateOutcome> ReconcileAsync(SqlAvailabilityGroup ag, AgHealthReport health, CancellationToken ct)
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
            metrics.SetRollingUpdateInProgress(name, false);
            if (wasInProgress)
                await eventPublisher.PublishAsync(ag, "RollingUpdateCompleted",
                    $"AG {name} rolling update to revision {targetRevision} completed", ct: ct);
            if (ru is null || ru.Phase != "Idle")
                ag.Status.RollingUpdate = new AgRollingUpdateStatus { Phase = "Idle", TotalPods = pods.Count, UpdatedPods = pods.Count, TargetRevision = targetRevision };
            return RollingUpdateOutcome.Idle;
        }

        metrics.SetRollingUpdateInProgress(name, true);

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

        // We only act when the *whole* AG is healthy. This is what enforces maxUnavailable=1 and
        // pauses the rollout the instant an incident appears (the failover path runs first).
        // The count checks are essential: a pod we just deleted is briefly absent from `pods` and a
        // resyncing replica from `ReplicaStates`; comparing against the expected replica count stops
        // us concluding "done" or deleting a second pod while the first is still missing/rebuilding.
        var expected = sts.Spec.Replicas ?? pods.Count;
        var clusterHealthy =
            pods.Count == expected
            && pods.All(p => p.Status?.ContainerStatuses?.All(c => c.Ready) == true)
            && health.PrimaryReplica != null
            && health.ReplicasMissingMembership.Count == 0
            && health.ReplicaStates.Count == expected
            && health.ReplicaStates.All(r =>
                r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
                && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));

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
            return RollingUpdateOutcome.InProgress;
        if (ru.NextActionAfter is { } gate && now < gate)
        {
            ru.Step = "Pausing";
            return RollingUpdateOutcome.InProgress;
        }

        // Choose the next stale pod: async secondaries first, then sync secondaries, highest ordinal
        // first; the primary is handled last via a planned failover.
        var primary = health.PrimaryReplica!;
        var staleSecondaries = health.ReplicaStates
            .Where(r => stalePods.Contains(r.Name) && !r.Name.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.AvailabilityMode.Equals("SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(r => Ordinal(r.Name))
            .Select(r => r.Name)
            .ToList();

        if (staleSecondaries.Count > 0)
        {
            var next = staleSecondaries[0];
            ru.Phase = "UpdatingSecondary";
            ru.CurrentPod = next;
            ru.CurrentStepStartedAt = now;
            ru.Step = "DeletingPod";
            logger.LogInformation("Rolling update: deleting secondary {Pod} (target revision {Rev})", next, targetRevision);
            await DeletePodAsync(next, ns, ct);
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
                await SetFailoverAnnotationAsync(ag, target.Name, ct);
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

    private async Task DeletePodAsync(string podName, string ns, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (pod != null)
            await kubernetesClient.DeleteAsync(pod, ct);
    }

    private async Task SetFailoverAnnotationAsync(SqlAvailabilityGroup ag, string target, CancellationToken ct)
    {
        ag.Metadata.Annotations ??= new Dictionary<string, string>();
        ag.Metadata.Annotations[AgFailoverOrchestrator.FailoverToAnnotation] = target;
        var updated = await kubernetesClient.UpdateAsync(ag, ct);
        ag.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
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
}
