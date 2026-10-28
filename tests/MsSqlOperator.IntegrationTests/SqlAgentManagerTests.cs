using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SqlAgentManagerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task CreateJob_WithDefaultCategory_Succeeds()
    {
        // Regression: the default job category was passed as "Uncategorized (Local)" without
        // the square brackets that are part of the real category name, so sp_add_job failed
        // with "The specified @category_name (...) does not exist." A null/empty category must
        // resolve to the built-in "[Uncategorized (Local)]" and create the job.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgentManager();
        var jobName = $"test_job_{Guid.NewGuid():N}"[..28];

        var act = async () => await manager.CreateJobAsync(connection, jobName, "regression", enabled: true, category: null, owner: null);
        await act();
        Assert.NotNull((await manager.GetJobIdAsync(connection, jobName)));

        await manager.DeleteJobAsync(connection, jobName);
    }

    [Fact]
    public async Task CreateJob_WithCustomCategory_CreatesCategoryThenJob()
    {
        // A previously unknown custom category must be created on demand rather than failing.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgentManager();
        var jobName = $"test_job_{Guid.NewGuid():N}"[..28];
        var category = $"cat_{Guid.NewGuid():N}"[..20];

        var act = async () => await manager.CreateJobAsync(connection, jobName, null, enabled: true, category: category, owner: null);
        await act();
        Assert.NotNull((await manager.GetJobIdAsync(connection, jobName)));

        await manager.DeleteJobAsync(connection, jobName);
    }

    private static JobStepDefinition Step(string name, int id, string subsystem = "TSQL")
        => new(name, id, subsystem, "master", "SELECT 1", "QuitWithSuccess", "QuitWithFailure", 0, 0);

    private static JobScheduleDefinition Daily(string name) => new(name, true, 4, 1, 0, 1, 0, 30000, null);

    private static async Task<object?> ScalarAsync(Microsoft.Data.SqlClient.SqlConnection connection, string sql, string job)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@job", job);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task ReplaceJobDefinition_ErrorMidway_LeavesThePreviousDefinitionIntactAndActive()
    {
        // An invalid step in
        // the middle of the replacement rolls the whole replacement back.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgentManager();
        var jobName = $"p007_job_{Guid.NewGuid():N}"[..28];
        await manager.ReplaceJobDefinitionAsync(connection, new JobDefinition(jobName, "v1 [mssqlop:1111111111111111]", true,
            null, null, [Step("first", 1)], [Daily($"{jobName}_daily")], null));

        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => manager.ReplaceJobDefinitionAsync(connection,
            new JobDefinition(jobName, "v2 [mssqlop:2222222222222222]", true, null, null,
                [Step("first", 1), Step("broken", 2, subsystem: "NoSuchSubsystem")], [], null)));

        Assert.Equal("v1 [mssqlop:1111111111111111]", await manager.GetJobDescriptionAsync(connection, jobName));
        Assert.Equal((true, 1), await manager.GetJobShapeAsync(connection, jobName));
        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id WHERE j.name = @job", jobName));
        await manager.DeleteJobAsync(connection, jobName);
    }

    [Fact]
    public async Task ReplaceJobDefinition_ScheduleSharedWithAnotherJob_Survives()
    {
        // sp_delete_schedule @force_delete = 1 deleted a schedule another job used too.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgentManager();
        var jobName = $"p007_own_{Guid.NewGuid():N}"[..28];
        var otherJob = $"p007_oth_{Guid.NewGuid():N}"[..28];
        var schedule = $"{jobName}_daily";
        await manager.ReplaceJobDefinitionAsync(connection, new JobDefinition(jobName, "v1 [mssqlop:1111111111111111]", true,
            null, null, [Step("first", 1)], [Daily(schedule)], null));
        await manager.CreateJobAsync(connection, otherJob, null, enabled: true, category: null, owner: null);
        await ScalarAsync(connection, $"EXEC msdb.dbo.sp_attach_schedule @job_name = @job, @schedule_name = N'{schedule}'", otherJob);

        await manager.ReplaceJobDefinitionAsync(connection, new JobDefinition(jobName, "v2 [mssqlop:2222222222222222]", true,
            null, null, [Step("first", 1)], [], null));

        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id WHERE j.name = @job", otherJob));
        await manager.DeleteJobAsync(connection, jobName);
        await manager.DeleteJobAsync(connection, otherJob);
    }
}
