using FluentAssertions;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection("SqlServer")]
public class SqlServerManagerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task GetCurrentConfig_ReturnsConfigDictionary()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var config = await manager.GetCurrentConfigAsync(connection);

        config.Should().NotBeEmpty();
        config.Should().ContainKey("max degree of parallelism");
    }

    [Fact]
    public async Task ApplySpConfigure_ChangesConfig()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        await manager.ApplySpConfigureAsync(connection, "max degree of parallelism", 2);
        await manager.ReconfigureAsync(connection);

        var config = await manager.GetCurrentConfigAsync(connection);
        config["max degree of parallelism"].Should().Be(2);
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

        databases.Should().Contain(dbName);
        databases.Should().NotContain("master");
        databases.Should().NotContain("tempdb");

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

        healthy.Should().BeTrue();
    }

    [Fact]
    public async Task TraceFlags_EnableAndDisable()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        await manager.EnableTraceFlagAsync(connection, 3226);
        var flags = await manager.GetActiveTraceFlagsAsync(connection);
        flags.Should().Contain(3226);

        await manager.DisableTraceFlagAsync(connection, 3226);
        flags = await manager.GetActiveTraceFlagsAsync(connection);
        flags.Should().NotContain(3226);
    }

    [Fact]
    public async Task GetTempDbFileCount_ReturnsPositive()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var count = await manager.GetTempDbFileCountAsync(connection);

        count.Should().BeGreaterThan(0);
    }
}
