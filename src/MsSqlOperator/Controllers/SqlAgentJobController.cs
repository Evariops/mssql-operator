using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlAgentJob), Verbs = RbacVerb.All)]
public class SqlAgentJobController(
    ILogger<SqlAgentJobController> logger,
    IKubernetesClient kubernetesClient,
    ISqlAgentManager sqlAgentManager,
    ISqlUserManager sqlUserManager,
    Services.ConnectionFactory.ISqlConnectionFactory connectionFactory,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null,
    Services.ConnectionFactory.TargetLimiter? targetLimiter = null) : IEntityController<SqlAgentJob>
{
    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    public async Task<ReconciliationResult<SqlAgentJob>> ReconcileAsync(SqlAgentJob entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlAgentJob {Name}", entity.Metadata.Name);

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
                // A slot of the target's DDL sessions, shared with the other kinds.
                await using var slot = await targetLimiter.ForTargetAsync(target.Kind, target.Namespace, target.Name, ct);

                // A privileged job is not something the author of the CR can grant itself —
                // allowDangerous is self-declared; the target must opt in as well.
                if (IsDangerous(job) && !await TargetAllowsPrivilegedAsync(target, ct))
                    throw new InvalidOperationException(
                        $"Job '{job.Name}' runs with sysadmin privileges or operating-system commands: the target {target.Kind}/{target.Name} " +
                        $"must carry the annotation {SqlIdentifierRules.AllowPrivilegedRolesAnnotation}=\"true\".");

                if (target.IsAvailabilityGroup)
                {
                    // Msdb is not replicated. The job used to exist on the primary of the day
                    // only: after a failover the new primary got a copy and the old one kept running
                    // its own, never cleaned up. Every replica carries the job, and a first step lets
                    // it run only where the replica is the primary.
                    // The replicas are opened in parallel; one that is down fails the target
                    // (retried) without holding the others behind its timeouts.
                    var replicaFailures = await TargetConnections.ForEveryServerAsync(target, connectionFactory, kubernetesClient,
                        (conn, _) => ProvisionJobAsync(entity, conn, job, entity.Status?.AppliedJobName, target.Name, ct), ct);
                    foreach (var (replica, error) in replicaFailures)
                        logger.LogWarning("Could not provision job {Job} on replica {Replica}: {Error}", job.Name, replica, error);

                    if (replicaFailures.Count > 0)
                        throw new InvalidOperationException(
                            $"job not provisioned on replica(s) {string.Join("; ", replicaFailures.Select(f => $"{f.Server}: {f.Error}"))}");
                }
                else
                {
                    await using var conn = await connectionFactory.OpenAsync(target.Host, target.Port, target.SaPassword, SqlProfile.Ddl, "master", ct);
                    await ProvisionJobAsync(entity, conn, job, entity.Status?.AppliedJobName, null, ct);
                }

                configured++;
                await eventPublisher.PublishAsync(entity, "JobConfigured", $"Job '{job.Name}' configured on {target.Kind}/{target.Name}", ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
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
        if (configured > 0 && entity.Spec.Job != null)
            entity.Status.AppliedJobName = entity.Spec.Job.Name; // Rename anchor
        ConditionOps.Set(entity.Status.Conditions ??= [], "Ready", condStatus, reason, message);
    }

    private async Task<ReconciliationResult<SqlAgentJob>> SaveAsync(SqlAgentJob entity, CancellationToken ct)
    {
        var updated = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return ReconciliationResult<SqlAgentJob>.Success(updated);
    }

    /// <summary>Hash marker appended to the msdb job description: the per-target,
    /// persisted record of the applied definition. Matching marker = no-op reconcile, so the
    /// job is no longer disabled/recreated (losing schedule triggers) on every pass.</summary>
    internal static string DescriptionWithHash(JobDefinitionSpec job, string? primaryGuardAg = null)
    {
        // The jobs whose last step used to fall through are rewritten once.
        var payload = k8s.KubernetesJson.Serialize(job) + (primaryGuardAg is null ? "" : $"|primary-guard:{primaryGuardAg}")
                      + (LastStepFallsThrough(job) ? "|last-step-quits" : "");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)))[..16];
        var marker = $"[mssqlop:{hash}]";
        return string.IsNullOrWhiteSpace(job.Description) ? marker : $"{job.Description} {marker}";
    }

    /// <summary>True when an msdb job carries the operator's marker, i.e. the operator created it
    /// Everything else belongs to someone else and is not ours to rewrite or drop.</summary>
    internal static bool IsOperatorManaged(string? description)
        => description?.Contains("[mssqlop:", StringComparison.Ordinal) == true;

    /// <summary>A job runs as sysadmin (owner unset, reserved, or member of sysadmin — checked at
    /// provisioning) or runs operating-system commands.</summary>
    private static bool IsDangerous(JobDefinitionSpec job)
        => string.IsNullOrWhiteSpace(job.Owner)
           || SqlIdentifierRules.IsReservedLogin(job.Owner)
           || job.AllowDangerous
           || job.Steps?.Any(s => s.Type != null && !s.Type.Equals("TSQL", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<bool> TargetAllowsPrivilegedAsync(Entities.Common.ResolvedTarget target, CancellationToken ct)
    {
        var annotations = await TargetConnections.GetAnnotationsAsync(target, kubernetesClient, ct);
        return SqlIdentifierRules.AllowsPrivilegedRoles(annotations);
    }

    private const string PrimaryGuardStepName = "mssqlop-primary-guard";

    /// <summary>First step of an AG job: fails — and the job quits with success — unless the
    /// local replica is the primary of the availability group.</summary>
    internal static string PrimaryGuardCommand(string agName)
        => "IF NOT EXISTS (SELECT 1 FROM sys.dm_hadr_availability_replica_states ars " +
           "JOIN sys.availability_groups ag ON ag.group_id = ars.group_id " +
           $"WHERE ag.name = {SqlQuoting.QuoteNStringLiteral(agName)} AND ars.is_local = 1 AND ars.role_desc = N'PRIMARY') " +
           $"THROW 50000, {SqlQuoting.QuoteNStringLiteral($"Not the primary replica of availability group {agName}: the job does not run here.")}, 1;";

    private async Task ProvisionJobAsync(SqlAgentJob entity, Microsoft.Data.SqlClient.SqlConnection connection, JobDefinitionSpec job, string? appliedJobName,
        string? primaryGuardAg, CancellationToken ct)
    {
        // job.name changed → delete the previously applied msdb job first, so the
        // rename never leaves an orphan running on the old name.
        if (appliedJobName != null && !appliedJobName.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
            await Gateway.DeleteJobAsync(entity, sqlAgentManager, connection, appliedJobName, connection.DataSource, ActuationIntent.SpecDeclared, ct);

        // An owner that is not 'sa' but a member of sysadmin runs the steps as sysadmin all
        // the same — it used to pass without allowDangerous.
        if (!job.AllowDangerous && !string.IsNullOrWhiteSpace(job.Owner)
            && (await sqlUserManager.GetServerRoleMembershipsAsync(connection, job.Owner, ct)).Contains("sysadmin", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"job.owner '{job.Owner}' is a member of sysadmin: the job's steps would run as sysadmin. " +
                "Set job.allowDangerous: true (the target must allow privileged grants too), or use a non-privileged owner.");

        var desiredDescription = DescriptionWithHash(job, primaryGuardAg);
        var existingJobId = await sqlAgentManager.GetJobIdAsync(connection, job.Name, ct);

        if (existingJobId != null)
        {
            var currentDescription = await sqlAgentManager.GetJobDescriptionAsync(connection, job.Name, ct);
            if (string.Equals(currentDescription, desiredDescription, StringComparison.Ordinal))
            {
                // Definition unchanged — leave the live job untouched. The marker is
                // written last, so it promises a complete definition; a job disabled or stripped of
                // steps by hand since is repaired.
                if (await sqlAgentManager.GetJobShapeAsync(connection, job.Name, ct) is { } shape
                    && shape.Enabled == job.Enabled && shape.StepCount == BuildSteps(job, primaryGuardAg).Count)
                    return;
            }
            // A job with this name already exists in msdb. If it carries no operator
            // marker, it belongs to a DBA — adopting it silently overwrites its steps and schedules,
            // and the finalizer would later DROP it. Refuse; adoption must be explicit.
            else if (!IsOperatorManaged(currentDescription))
                throw new JobNameConflictException(
                    $"A SQL Agent job named '{job.Name}' already exists in msdb and was not created by the operator " +
                    "(its description carries no [mssqlop:…] marker). Refusing to overwrite its steps and schedules — " +
                    "and it would be dropped when this SqlAgentJob is deleted. Rename the job, or remove the existing one.");
        }

        // One transaction for the whole definition; an error leaves the previous one intact.
        JobNotifications? notifications = null;
        if (job.Notifications != null)
        {
            var (notifyLevelEmail, emailOp, notifyLevelEventLog, notifyLevelPage, pageOp) = MapNotifications(job.Notifications);
            notifications = new JobNotifications(notifyLevelEmail, emailOp, notifyLevelEventLog, notifyLevelPage, pageOp);
        }
        await sqlAgentManager.ReplaceJobDefinitionAsync(connection, new JobDefinition(
            job.Name, desiredDescription, job.Enabled, job.Category, job.Owner,
            BuildSteps(job, primaryGuardAg), (job.Schedules ?? []).Select(BuildSchedule).ToList(), notifications), ct);
        // Live run state (last/next run) is no longer persisted to status — served by the APIService.
    }

    /// <summary>GoToNextStep — the default — on the last step. SQL Agent then fails the
    /// run ("tried to run a non-existent step"); SSMS writes a quit there instead.</summary>
    internal static bool LastStepFallsThrough(JobDefinitionSpec job)
        => (job.Steps ?? []).OrderBy(s => s.Order).LastOrDefault() is { } last
           && (IsGoToNextStep(last.OnSuccess) || IsGoToNextStep(last.OnFailure));

    private static bool IsGoToNextStep(string action) => action.Equals("GoToNextStep", StringComparison.OrdinalIgnoreCase);

    /// <summary>The msdb steps of a job: contiguous ids in the user's order, GoToStep targets
    /// (given as the target step's <c>order</c>) mapped to those ids, the primary guard
    /// first for an availability group, and the last step quitting where it would fall through
    /// </summary>
    internal static List<JobStepDefinition> BuildSteps(JobDefinitionSpec job, string? primaryGuardAg)
    {
        var ordered = (job.Steps ?? []).OrderBy(s => s.Order).ToList();
        var offset = primaryGuardAg is null ? 1 : 2;
        var idByOrder = ordered.Select((s, i) => (s.Order, Id: i + offset)).ToDictionary(x => x.Order, x => x.Id);

        int? Target(int? order, string stepName) => order is null
            ? null
            : idByOrder.TryGetValue(order.Value, out var id)
                ? id
                : throw new InvalidOperationException($"Step '{stepName}' jumps to step {order}, which does not exist.");

        var steps = new List<JobStepDefinition>();
        if (primaryGuardAg is not null)
            steps.Add(new JobStepDefinition(PrimaryGuardStepName, 1, "TSQL", "master", PrimaryGuardCommand(primaryGuardAg),
                "GoToNextStep", "QuitWithSuccess", 0, 0));

        foreach (var step in ordered)
        {
            var last = ReferenceEquals(step, ordered[^1]);
            var onSuccess = last && IsGoToNextStep(step.OnSuccess) ? "QuitWithSuccess" : step.OnSuccess;
            var onFailure = last && IsGoToNextStep(step.OnFailure) ? "QuitWithFailure" : step.OnFailure;
            steps.Add(new JobStepDefinition(step.Name, idByOrder[step.Order], step.Type, step.Database, step.Command,
                onSuccess, onFailure, step.RetryAttempts, step.RetryIntervalMinutes,
                step.OnSuccess.Equals("GoToStep", StringComparison.OrdinalIgnoreCase) ? Target(step.OnSuccessStep, step.Name) : null,
                step.OnFailure.Equals("GoToStep", StringComparison.OrdinalIgnoreCase) ? Target(step.OnFailureStep, step.Name) : null));
        }
        return steps;
    }

    /// <summary>msdb schedule fields from the spec.</summary>
    internal static JobScheduleDefinition BuildSchedule(JobScheduleSpec schedule)
    {
        var freqType = schedule.FrequencyType switch { "Once" => 1, "Daily" => 4, "Weekly" => 8, "Monthly" => 16, _ => 4 };
        var subdayType = schedule.SubdayType switch { "Seconds" => 2, "Minutes" => 4, "Hours" => 8, _ => 1 };
        return new JobScheduleDefinition(
            schedule.Name,
            schedule.Enabled,
            freqType,
            schedule.FrequencyInterval,
            freqType is 8 or 16 ? Math.Max(1, schedule.RecurrenceFactor) : 0,
            subdayType,
            subdayType == 1 ? 0 : schedule.SubdayInterval,
            ParseStartTime(schedule.StartTime),
            schedule.StartDate is { } date && int.TryParse(date, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null);
    }

    /// <summary>
    /// The schedule start time as msdb's HHMMSS integer. The validator requires HHmmss, but
    /// the value went through TimeSpan.TryParse, which reads "083000" as 83 000 days — every schedule
    /// started at midnight. HH:mm:ss is still accepted.
    /// </summary>
    internal static int ParseStartTime(string? startTime)
    {
        if (string.IsNullOrEmpty(startTime))
            return 0;
        if (startTime.Length == 6 && int.TryParse(startTime, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hhmmss))
            return hhmmss;
        return TimeSpan.TryParseExact(startTime, @"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture, out var ts)
            ? ts.Hours * 10000 + ts.Minutes * 100 + ts.Seconds
            : 0;
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
