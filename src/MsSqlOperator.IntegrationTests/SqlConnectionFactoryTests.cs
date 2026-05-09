using FluentAssertions;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.IntegrationTests;

[Collection("SqlServer")]
public class SqlConnectionFactoryTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task CreateConnectionAsync_ReturnsOpenConnection()
    {
        var factory = new SqlConnectionFactory();

        await using var connection = await factory.CreateConnectionAsync(fixture.Host, fixture.Port, fixture.Password);

        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public void BuildConnectionString_ContainsHostAndPort()
    {
        var factory = new SqlConnectionFactory();

        var connStr = factory.BuildConnectionString(fixture.Host, fixture.Port, fixture.Password, "master");

        connStr.Should().Contain(fixture.Host);
        connStr.Should().Contain(fixture.Port.ToString());
    }
}
