using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlAgentManager"/>: every job carries the operator's
/// marker unless told otherwise, and deletions are recorded per server (connection DataSource).</summary>
public sealed class FakeSqlAgentManager : ISqlAgentManager
{
    public readonly List<(string Host, string Job)> DeleteJobCalls = [];
    public string? Description { get; set; } = "nightly [mssqlop:0123456789abcdef]";

    /// <summary>Makes DeleteJobAsync fail on the connections it returns true for.</summary>
    public Func<SqlConnection, bool>? DeleteFails { get; set; }

    public Task<bool> IsAgentRunningAsync(SqlConnection connection, CancellationToken ct = default) => Task.FromResult(true);
    public Task<Guid?> GetJobIdAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
    public Task<string?> GetJobDescriptionAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.FromResult(Description);
    public Task<Guid> CreateJobAsync(SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default)
        => Task.FromResult(Guid.NewGuid());
    public Task UpdateJobAsync(SqlConnection connection, string jobName, string? description, bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteJobAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        if (DeleteFails?.Invoke(connection) == true)
            throw new InvalidOperationException("fake: sp_delete_job failed");
        DeleteJobCalls.Add((connection.DataSource, jobName));
        return Task.CompletedTask;
    }

    public Task CreateJobStepAsync(SqlConnection connection, string jobName, JobStepDefinition step, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAllJobStepsAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateScheduleAsync(SqlConnection connection, string jobName, JobScheduleDefinition schedule, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAllSchedulesAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
        => Task.FromResult<(DateTime?, string?, DateTime?)>((null, null, null));
    public Task SetJobNotificationAsync(SqlConnection connection, string jobName, int notifyLevelEmail, string? emailOperatorName, int notifyLevelEventLog, int notifyLevelPage, string? pageOperatorName, CancellationToken ct = default)
        => Task.CompletedTask;
}
