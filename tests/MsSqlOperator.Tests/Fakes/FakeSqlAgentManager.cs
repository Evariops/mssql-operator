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
    public Task<Guid?> GetJobIdAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.FromResult(JobId);
    public Task<string?> GetJobDescriptionAsync(SqlConnection connection, string jobName, CancellationToken ct = default) => Task.FromResult(Description);
    public Task<Guid> CreateJobAsync(SqlConnection connection, string jobName, string? description, bool enabled, string? category, string? owner, CancellationToken ct = default)
        => Task.FromResult(Guid.NewGuid());

    public Task DeleteJobAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
    {
        if (DeleteFails?.Invoke(connection) == true)
            throw new InvalidOperationException("fake: sp_delete_job failed");
        DeleteJobCalls.Add((connection.DataSource, jobName));
        return Task.CompletedTask;
    }

    public Task<(DateTime? lastRun, string? lastStatus, DateTime? nextRun)> GetJobStatusAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
        => Task.FromResult<(DateTime?, string?, DateTime?)>((null, null, null));
    /// <summary>Enabled state and step count of the job, as msdb reports them; null: no such job.</summary>
    public (bool Enabled, int StepCount)? Shape { get; set; }
    public Task<(bool Enabled, int StepCount)?> GetJobShapeAsync(SqlConnection connection, string jobName, CancellationToken ct = default)
        => Task.FromResult(Shape);

    /// <summary>Id the job exists under in msdb, or null when it does not exist.</summary>
    public Guid? JobId { get; set; }

    public readonly List<JobDefinition> Replacements = [];
    public Task ReplaceJobDefinitionAsync(SqlConnection connection, JobDefinition definition, CancellationToken ct = default)
    {
        Replacements.Add(definition);
        return Task.CompletedTask;
    }
}
