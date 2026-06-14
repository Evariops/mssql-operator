using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SqlServerManagerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetCurrentConfig_ReturnsConfigDictionary()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var config = await manager.GetCurrentConfigAsync(connection);

        Assert.NotEmpty(config);
        Assert.Contains("max degree of parallelism", config);
    }

    [Fact]
    public async Task ApplySpConfigure_ChangesConfig()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        await manager.ApplySpConfigureAsync(connection, "max degree of parallelism", 2);
        await manager.ReconfigureAsync(connection);

        var config = await manager.GetCurrentConfigAsync(connection);
        Assert.Equal(2, config["max degree of parallelism"]);
    }

    [Fact]
    public async Task GetDatabaseNames_ReturnsUserDatabasesOnly()
    {
        // GetDatabaseNamesAsync returns user databases (database_id > 4); system databases
        // are intentionally excluded. (The previous test asserted the opposite, which never
        // matched the method's contract.)
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();
        var dbName = $"udb_{Guid.NewGuid():N}"[..20];

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE [{dbName}]";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var databases = await manager.GetDatabaseNamesAsync(connection);

        Assert.Contains(dbName, databases);
        Assert.DoesNotContain("master", databases);
        Assert.DoesNotContain("tempdb", databases);

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = $"DROP DATABASE [{dbName}]";
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task IsHealthy_ReturnsTrue()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var healthy = await manager.IsHealthyAsync(connection);

        Assert.True(healthy);
    }

    [Fact]
    public async Task TraceFlags_EnableAndDisable()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        await manager.EnableTraceFlagAsync(connection, 3226);
        var flags = await manager.GetActiveTraceFlagsAsync(connection);
        Assert.Contains(3226, flags);

        await manager.DisableTraceFlagAsync(connection, 3226);
        flags = await manager.GetActiveTraceFlagsAsync(connection);
        Assert.DoesNotContain(3226, flags);
    }

    [Fact]
    public async Task GetTempDbFileCount_ReturnsPositive()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var count = await manager.GetTempDbFileCountAsync(connection);

        Assert.True(count > 0);
    }
}
