using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
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
    IEventPublisher eventPublisher) : IEntityController<SqlAgentJob>
{
    public async Task<ReconciliationResult<SqlAgentJob>> ReconcileAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlAgentJob {Name}", entity.Metadata.Name);

        var job = entity.Spec.Job;
        if (job == null) return ReconciliationResult<SqlAgentJob>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        entity.Status ??= new SqlAgentJobStatus();
        entity.Status.TargetStatuses ??= [];

        if (resolvedTargets.Count == 0)
        {
            SetCondition(entity, "NoTargetsFound", "True", "NoMatch", "No targets matched the selector");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlAgentJob>.Success(entity);
        }

        var hasError = false;
        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            try
            {
                // For AG, find primary (SQL Agent only runs on primary)
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null)
                    {
                        targetStatus.LastError = "No primary replica found";
                        hasError = true;
                        continue;
                    }

                    await using var conn = primary.Value.Connection;
                    await ProvisionJobAsync(conn, job, targetStatus, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    await ProvisionJobAsync(conn, job, targetStatus, ct);
                }

                targetStatus.LastError = null;
            }
            catch (Exception ex)
            {
                targetStatus.LastError = ex.Message;
                hasError = true;
                logger.LogWarning(ex, "Error provisioning job on {Kind}/{Name}", target.Kind, target.Name);
            }
        }

        entity.Status.Created = entity.Status.TargetStatuses.Any(t => t.Created);
        SetCondition(entity, "Ready", hasError ? "False" : "True", hasError ? "PartialError" : "JobConfigured",
            $"Job '{job.Name}' configured on {resolvedTargets.Count} target(s)");

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "JobConfigured", $"Job '{job.Name}' configured on {resolvedTargets.Count} target(s)", ct: ct);
        return ReconciliationResult<SqlAgentJob>.Success(entity);
    }

    public Task<ReconciliationResult<SqlAgentJob>> DeletedAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAgentJob {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAgentJob>.Success(entity));
    }

    private async Task ProvisionJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, JobDefinitionSpec job, AgentJobTargetStatus targetStatus, CancellationToken ct)
    {
        var existingJobId = await sqlAgentManager.GetJobIdAsync(connection, job.Name, ct);

        if (existingJobId == null)
        {
            var jobId = await sqlAgentManager.CreateJobAsync(connection, job.Name, job.Description, job.Enabled, job.Category, job.Owner, ct);
            existingJobId = jobId;
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

        // Update target status
        var (lastRun, lastStatus, nextRun) = await sqlAgentManager.GetJobStatusAsync(connection, job.Name, ct);
        targetStatus.JobId = existingJobId?.ToString();
        targetStatus.Created = true;
        targetStatus.LastRunDate = lastRun;
        targetStatus.LastRunStatus = lastStatus;
        targetStatus.NextRunDate = nextRun;
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

    private static AgentJobTargetStatus GetOrCreateTargetStatus(SqlAgentJob entity, ResolvedTarget target)
    {
        var existing = entity.Status!.TargetStatuses!.FirstOrDefault(t => t.Kind == target.Kind && t.Name == target.Name && t.Namespace == target.Namespace);
        if (existing != null) return existing;

        var status = new AgentJobTargetStatus { Kind = target.Kind, Name = target.Name, Namespace = target.Namespace };
        entity.Status.TargetStatuses!.Add(status);
        return status;
    }

    private static void SetCondition(SqlAgentJob entity, string type, string status, string reason, string? message)
    {
        entity.Status!.Conditions ??= [];
        var existing = entity.Status.Conditions.FirstOrDefault(c => c.Type == type);
        if (existing != null)
        {
            existing.Status = status; existing.Reason = reason; existing.Message = message; existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            entity.Status.Conditions.Add(new ConditionStatus { Type = type, Status = status, Reason = reason, Message = message, LastTransitionTime = DateTime.UtcNow });
        }
    }
}
