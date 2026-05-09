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
    public async Task GetDatabaseNames_ReturnsSystemDatabases()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlServerManager();

        var databases = await manager.GetDatabaseNamesAsync(connection);

        databases.Should().Contain("master");
        databases.Should().Contain("tempdb");
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
