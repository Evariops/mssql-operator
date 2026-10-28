namespace MsSqlOperator.Services.SqlServer;

/// <summary>A job step as msdb.dbo.sp_add_jobstep takes it (actions already mapped by the caller's
/// names: QuitWithSuccess, QuitWithFailure, GoToNextStep, GoToStep).</summary>
public sealed record JobStepDefinition(
    string Name, int StepId, string Subsystem, string? Database, string Command,
    string OnSuccess, string OnFailure, int RetryAttempts, int RetryIntervalMinutes,
    int? OnSuccessStepId = null, int? OnFailStepId = null);

/// <summary>A job schedule as msdb.dbo.sp_add_jobschedule takes it.</summary>
/// <param name="Name">Schedule name.</param>
/// <param name="Enabled">Whether the schedule fires.</param>
/// <param name="FreqType">1 once, 4 daily, 8 weekly, 16 monthly.</param>
/// <param name="FreqInterval">Days between runs (daily), day bitmask (weekly), day of month (monthly).</param>
/// <param name="RecurrenceFactor">Weeks or months between runs — required (≥ 1) for weekly and monthly.</param>
/// <param name="SubdayType">1 at the start time only, 2 seconds, 4 minutes, 8 hours.</param>
/// <param name="SubdayInterval">Seconds/minutes/hours between runs within the day.</param>
/// <param name="ActiveStartTime">HHMMSS as an integer.</param>
/// <param name="ActiveStartDate">YYYYMMDD as an integer, or null for today.</param>
public sealed record JobScheduleDefinition(
    string Name, bool Enabled, int FreqType, int FreqInterval, int RecurrenceFactor,
    int SubdayType, int SubdayInterval, int ActiveStartTime, int? ActiveStartDate);

/// <summary>The notifications of a job, as msdb.dbo.sp_update_job takes them.</summary>
public sealed record JobNotifications(int Email, string? EmailOperator, int EventLog, int Page, string? PageOperator);

/// <summary>Everything that defines a job, replaced in one transaction.</summary>
public sealed record JobDefinition(
    string Name, string Description, bool Enabled, string? Category, string? Owner,
    IReadOnlyList<JobStepDefinition> Steps, IReadOnlyList<JobScheduleDefinition> Schedules, JobNotifications? Notifications);

public interface ISqlAgentManager
{
    Task<bool> IsAgentRunningAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<Guid?> GetJobIdAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);

    /// <summary>Current msdb description of the job, or null when the job does not exist.</summary>
    Task<string?> GetJobDescriptionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);

    /// <summary>Whether the job is enabled and how many steps it has, or null when it does not exist:
    /// what a job changed by hand under an unchanged marker gives away.</summary>
    Task<(bool Enabled, int StepCount)?> GetJobShapeAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);

    Task<Guid> CreateJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default);

    /// <summary>
    /// Creates the job, or replaces its whole definition, in ONE transaction (SET XACT_ABORT
    /// ON): disabled first, steps and schedules replaced, notifications, enabled state, and the
    /// description carrying the marker last. Any error leaves the previous definition intact and
    /// active. Schedules are detached (sp_detach_schedule @delete_unused_schedule = 1), never
    /// force-deleted: a schedule shared with another job survives.
    /// </summary>
    Task ReplaceJobDefinitionAsync(Microsoft.Data.SqlClient.SqlConnection connection, JobDefinition definition, CancellationToken ct = default);

    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DeleteJob)]
    Task DeleteJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
}
