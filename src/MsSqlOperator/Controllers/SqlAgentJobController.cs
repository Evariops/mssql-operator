using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlAgentJob), Verbs = RbacVerb.All)]
public class SqlAgentJobController(
    ILogger<SqlAgentJobController> logger,
    IKubernetesClient kubernetesClient,
    ISqlAgentManager sqlAgentManager,
    Services.ConnectionFactory.ISqlConnectionFactory connectionFactory,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics) : IEntityController<SqlAgentJob>
{
    public async Task<ReconciliationResult<SqlAgentJob>> ReconcileAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlAgentJob {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var job = entity.Spec.Job;

        entity.Status ??= new SqlAgentJobStatus();
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;

        if (job == null)
        {
            // The webhook rejects a missing job; guard defensively without touching SQL.
            Finish(entity, "Pending", "False", "NoJob", "job definition is required", 0, 0);
            metrics.SetTargets("SqlAgentJob", ns, name, 0, 0, 0);
            return await SaveAsync(entity, ct);
        }

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        if (resolvedTargets.Count == 0)
        {
            Finish(entity, "Pending", "False", "NoMatch", "No targets matched the selector", 0, 0);
            metrics.SetTargets("SqlAgentJob", ns, name, 0, 0, 0);
            // Keep polling until a target appears.
            var saved = await SaveAsync(entity, ct);
            return MsSqlOperator.Operator.Abstractions.ReconciliationResult<MsSqlOperator.Entities.V1Alpha1.SqlAgentJob>.Success(saved.Entity!, TimeSpan.FromSeconds(60));
        }

        var configured = 0;
        var failed = 0;
        foreach (var target in resolvedTargets)
        {
            try
            {
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    // SQL Agent jobs run on the primary replica only.
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null)
                    {
                        failed++;
                        await eventPublisher.PublishAsync(entity, "TargetUnreachable", $"No primary replica for {target.Kind}/{target.Name}", "Warning", ct);
                        continue;
                    }

                    await using var conn = primary.Value.Connection;
                    await ProvisionJobAsync(conn, job, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    await ProvisionJobAsync(conn, job, ct);
                }

                configured++;
                await eventPublisher.PublishAsync(entity, "JobConfigured", $"Job '{job.Name}' configured on {target.Kind}/{target.Name}", ct: ct);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Error provisioning job on {Kind}/{Name}", target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "JobConfigurationFailed", $"{target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        var phase = failed == 0 ? "Ready" : configured > 0 ? "PartiallyConfigured" : "Failed";
        var (status, reason, message) = failed == 0
            ? ("True", "JobConfigured", $"Job '{job.Name}' configured on {configured} target(s)")
            : ("False", "PartialError", $"Job '{job.Name}' configured on {configured}/{resolvedTargets.Count} target(s); {failed} failed");
        Finish(entity, phase, status, reason, message, configured, resolvedTargets.Count);
        metrics.SetTargets("SqlAgentJob", ns, name, configured, resolvedTargets.Count, failed);

        var result = await SaveAsync(entity, ct);
        return failed > 0
            ? ReconciliationResult<SqlAgentJob>.Success(result.Entity, TimeSpan.FromSeconds(30))
            : result;
    }

    public Task<ReconciliationResult<SqlAgentJob>> DeletedAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAgentJob {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlAgentJob", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAgentJob>.Success(entity));
    }

    private static void Finish(SqlAgentJob entity, string phase, string condStatus, string reason, string message, int configured, int total)
    {
        entity.Status!.Phase = phase;
        entity.Status.TotalTargets = total;
        entity.Status.ConfiguredTargets = configured;
        ConditionOps.Set(entity.Status.Conditions ??= [], "Ready", condStatus, reason, message);
    }

    private async Task<ReconciliationResult<SqlAgentJob>> SaveAsync(SqlAgentJob entity, CancellationToken ct)
    {
        var updated = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return ReconciliationResult<SqlAgentJob>.Success(updated);
    }

    private async Task ProvisionJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, JobDefinitionSpec job, CancellationToken ct)
    {
        var existingJobId = await sqlAgentManager.GetJobIdAsync(connection, job.Name, ct);

        if (existingJobId == null)
        {
            await sqlAgentManager.CreateJobAsync(connection, job.Name, job.Description, job.Enabled, job.Category, job.Owner, ct);
        }
        else
        {
            // Disable the job during mutation to prevent running with partial steps/schedules
            await sqlAgentManager.UpdateJobAsync(connection, job.Name, job.Description, false, ct);
        }

        // Recreate steps
        await sqlAgentManager.DeleteAllJobStepsAsync(connection, job.Name, ct);
        if (job.Steps != null)
        {
            foreach (var step in job.Steps.OrderBy(s => s.Order))
                await sqlAgentManager.CreateJobStepAsync(connection, job.Name, step.Name, step.Order, step.Type, step.Database, step.Command, step.OnSuccess, step.OnFailure, step.RetryAttempts, step.RetryIntervalMinutes, ct);
        }

        // Recreate schedules
        await sqlAgentManager.DeleteAllSchedulesAsync(connection, job.Name, ct);
        if (job.Schedules != null)
        {
            foreach (var schedule in job.Schedules)
            {
                var freqType = schedule.FrequencyType switch { "Once" => 1, "Daily" => 4, "Weekly" => 8, "Monthly" => 16, _ => 4 };
                await sqlAgentManager.CreateScheduleAsync(connection, job.Name, schedule.Name, schedule.Enabled, freqType, schedule.FrequencyInterval, schedule.StartTime, ct);
            }
        }

        // Re-enable job with desired enabled state after all steps/schedules are in place
        await sqlAgentManager.UpdateJobAsync(connection, job.Name, job.Description, job.Enabled, ct);

        // Apply notifications
        if (job.Notifications != null)
        {
            var (notifyLevelEmail, emailOp, notifyLevelEventLog, notifyLevelPage, pageOp) = MapNotifications(job.Notifications);
            await sqlAgentManager.SetJobNotificationAsync(connection, job.Name, notifyLevelEmail, emailOp, notifyLevelEventLog, notifyLevelPage, pageOp, ct);
        }
        // Live run state (last/next run) is no longer persisted to status — served by the APIService.
    }

    private static (int notifyLevelEmail, string? emailOp, int notifyLevelEventLog, int notifyLevelPage, string? pageOp) MapNotifications(JobNotificationSpec notifications)
    {
        var notifyLevelEmail = 0;
        string? emailOp = null;
        var notifyLevelEventLog = 0;
        var notifyLevelPage = 0;
        string? pageOp = null;

        if (notifications.OnFailure != null)
        {
            if (notifications.OnFailure.NotifyMethod?.Equals("Email", StringComparison.OrdinalIgnoreCase) == true)
            {
                notifyLevelEmail = notifications.OnCompletion != null ? 3 : 2; // Always if both, else OnFailure
                emailOp = notifications.OnFailure.Operator;
            }
            else if (notifications.OnFailure.NotifyMethod?.Equals("Pager", StringComparison.OrdinalIgnoreCase) == true)
            {
                notifyLevelPage = notifications.OnCompletion != null ? 3 : 2;
                pageOp = notifications.OnFailure.Operator;
            }
            if (notifications.OnFailure.WriteToEventLog)
                notifyLevelEventLog = 2;
        }

        if (notifications.OnCompletion != null)
        {
            if (notifications.OnCompletion.NotifyMethod?.Equals("Email", StringComparison.OrdinalIgnoreCase) == true && notifyLevelEmail == 0)
            {
                notifyLevelEmail = 3;
                emailOp = notifications.OnCompletion.Operator;
            }
            if (notifications.OnCompletion.WriteToEventLog && notifyLevelEventLog == 0)
                notifyLevelEventLog = 3;
        }

        return (notifyLevelEmail, emailOp, notifyLevelEventLog, notifyLevelPage, pageOp);
    }
}
