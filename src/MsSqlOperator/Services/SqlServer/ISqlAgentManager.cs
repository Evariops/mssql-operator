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

public interface ISqlAgentManager
{
    Task<bool> IsAgentRunningAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<Guid?> GetJobIdAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);

    /// <summary>Current msdb description of the job, or null when the job does not exist.</summary>
    Task<string?> GetJobDescriptionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task<Guid> CreateJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default);
    Task UpdateJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string? description, bool enabled, CancellationToken ct = default);
    Task DeleteJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task CreateJobStepAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, JobStepDefinition step, CancellationToken ct = default);
    Task DeleteAllJobStepsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task CreateScheduleAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, JobScheduleDefinition schedule, CancellationToken ct = default);
    Task DeleteAllSchedulesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task SetJobNotificationAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, int notifyLevelEmail, string? emailOperatorName, int notifyLevelEventLog, int notifyLevelPage, string? pageOperatorName, CancellationToken ct = default);
}
