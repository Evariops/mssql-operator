using FluentAssertions;
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
        await act.Should().NotThrowAsync();
        (await manager.GetJobIdAsync(connection, jobName)).Should().NotBeNull();

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
        await act.Should().NotThrowAsync();
        (await manager.GetJobIdAsync(connection, jobName)).Should().NotBeNull();

        await manager.DeleteJobAsync(connection, jobName);
    }
}
