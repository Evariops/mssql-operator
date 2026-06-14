using Microsoft.Extensions.Options;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SqlConnectionFactoryTests(SqlServerFixture fixture)
{
    private static SqlConnectionFactory CreateFactory() =>
        new(Options.Create(new SqlConnectionOptions()));

    [Fact]
    public async Task CreateConnectionAsync_ReturnsOpenConnection()
    {
        var factory = CreateFactory();

        await using var connection = await factory.CreateConnectionAsync(fixture.Host, fixture.Port, fixture.Password);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public void BuildConnectionString_ContainsHostAndPort()
    {
        var factory = CreateFactory();

        var connStr = factory.BuildConnectionString(fixture.Host, fixture.Port, fixture.Password, "master");

        Assert.Contains(fixture.Host, connStr);
        Assert.Contains(fixture.Port.ToString(), connStr);
    }
}
