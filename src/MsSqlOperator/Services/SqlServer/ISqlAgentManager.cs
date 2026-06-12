namespace MsSqlOperator.Services.SqlServer;

public interface ISqlAgentManager
{
    Task<bool> IsAgentRunningAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<Guid?> GetJobIdAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);

    /// <summary>Current msdb description of the job, or null when the job does not exist.</summary>
    Task<string?> GetJobDescriptionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task<Guid> CreateJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default);
    Task UpdateJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string? description, bool enabled, CancellationToken ct = default);
    Task DeleteJobAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task CreateJobStepAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string stepName, int stepId, string subsystem, string? database, string command, string onSuccess, string onFailure, int retryAttempts, int retryInterval, CancellationToken ct = default);
    Task DeleteAllJobStepsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task CreateScheduleAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, string scheduleName, bool enabled, int freqType, int freqInterval, string? activeStartTime, CancellationToken ct = default);
    Task DeleteAllSchedulesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, CancellationToken ct = default);
    Task SetJobNotificationAsync(Microsoft.Data.SqlClient.SqlConnection connection, string jobName, int notifyLevelEmail, string? emailOperatorName, int notifyLevelEventLog, int notifyLevelPage, string? pageOperatorName, CancellationToken ct = default);
}
