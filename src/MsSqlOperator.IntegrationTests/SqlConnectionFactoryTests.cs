using FluentAssertions;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.IntegrationTests;

[Collection("SqlServer")]
public class SqlConnectionFactoryTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static SqlConnectionFactory CreateFactory() =>
        new(Options.Create(new SqlConnectionOptions()));

    [Fact]
    public async Task CreateConnectionAsync_ReturnsOpenConnection()
    {
        var factory = CreateFactory();

        await using var connection = await factory.CreateConnectionAsync(fixture.Host, fixture.Port, fixture.Password);

        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public void BuildConnectionString_ContainsHostAndPort()
    {
        var factory = CreateFactory();

        var connStr = factory.BuildConnectionString(fixture.Host, fixture.Port, fixture.Password, "master");

        connStr.Should().Contain(fixture.Host);
        connStr.Should().Contain(fixture.Port.ToString());
    }
}
