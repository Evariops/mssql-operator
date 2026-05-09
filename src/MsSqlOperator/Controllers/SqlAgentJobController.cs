using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlAgentJob), Verbs = RbacVerb.All)]
public class SqlAgentJobController(
    ILogger<SqlAgentJobController> logger,
    IKubernetesClient kubernetesClient,
    ISqlAgentManager sqlAgentManager,
    Services.ConnectionFactory.ISqlConnectionFactory connectionFactory,
    Services.Kubernetes.IEventPublisher eventPublisher) : IEntityController<SqlAgentJob>
{
    public async Task<ReconciliationResult<SqlAgentJob>> ReconcileAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlAgentJob {Name}", entity.Metadata.Name);

        var job = entity.Spec.Job;
        if (job == null) return ReconciliationResult<SqlAgentJob>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        var (host, port, password) = await ResolveTargetAsync(entity, ns, ct);
        if (host == null || password == null) return ReconciliationResult<SqlAgentJob>.Success(entity);

        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        var existingJobId = await sqlAgentManager.GetJobIdAsync(connection, job.Name, ct);

        if (existingJobId == null)
        {
            var jobId = await sqlAgentManager.CreateJobAsync(connection, job.Name, job.Description, job.Enabled, job.Category, job.Owner, ct);
            logger.LogInformation("Created SQL Agent job {JobName} with ID {JobId}", job.Name, jobId);
            existingJobId = jobId;
        }
        else
        {
            await sqlAgentManager.UpdateJobAsync(connection, job.Name, job.Description, job.Enabled, ct);
        }

        // Recreate steps
        await sqlAgentManager.DeleteAllJobStepsAsync(connection, job.Name, ct);
        if (job.Steps != null)
        {
            foreach (var step in job.Steps.OrderBy(s => s.Order))
            {
                await sqlAgentManager.CreateJobStepAsync(connection, job.Name, step.Name, step.Order, step.Type, step.Database, step.Command, step.OnSuccess, step.OnFailure, step.RetryAttempts, step.RetryIntervalMinutes, ct);
            }
        }

        // Recreate schedules
        await sqlAgentManager.DeleteAllSchedulesAsync(connection, job.Name, ct);
        if (job.Schedules != null)
        {
            foreach (var schedule in job.Schedules)
            {
                var freqType = schedule.FrequencyType switch
                {
                    "Once" => 1, "Daily" => 4, "Weekly" => 8, "Monthly" => 16, _ => 4
                };
                await sqlAgentManager.CreateScheduleAsync(connection, job.Name, schedule.Name, schedule.Enabled, freqType, schedule.FrequencyInterval, schedule.StartTime, ct);
            }
        }

        // Update status
        var (lastRun, lastStatus, nextRun) = await sqlAgentManager.GetJobStatusAsync(connection, job.Name, ct);
        entity.Status ??= new SqlAgentJobStatus();
        entity.Status.JobId = existingJobId?.ToString();
        entity.Status.Created = true;
        entity.Status.LastRunDate = lastRun;
        entity.Status.LastRunStatus = lastStatus;
        entity.Status.NextRunDate = nextRun;
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready", Status = "True", Reason = "JobConfigured",
                Message = $"Job '{job.Name}' configured with {job.Steps?.Count ?? 0} steps",
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "JobConfigured", $"Job '{job.Name}' configured with {job.Steps?.Count ?? 0} steps", ct: ct);
        return ReconciliationResult<SqlAgentJob>.Success(entity);
    }

    public Task<ReconciliationResult<SqlAgentJob>> DeletedAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAgentJob {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAgentJob>.Success(entity));
    }

    private async Task<(string? host, int port, string? password)> ResolveTargetAsync(SqlAgentJob entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.InstanceRef == null) return (null, 1433, null);
        var instance = await kubernetesClient.GetAsync<SqlInstance>(entity.Spec.InstanceRef.Name, ns, ct);
        if (instance?.Spec.SaPasswordSecretRef == null) return (null, 1433, null);
        try
        {
            var secret = await kubernetesClient.GetAsync<k8s.Models.V1Secret>(instance.Spec.SaPasswordSecretRef.Name, ns, ct);
            var pwd = secret?.Data?.TryGetValue(instance.Spec.SaPasswordSecretRef.Key, out var bytes) == true
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
            return ($"{entity.Spec.InstanceRef.Name}.{ns}.svc.cluster.local", instance.Spec.Service?.Port ?? 1433, pwd);
        }
        catch { return (null, 1433, null); }
    }
}
