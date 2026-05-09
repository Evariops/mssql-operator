using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MsSqlOperator.IntegrationTests;

public class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();
    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(1433);
    public string Password => MsSqlBuilder.DefaultPassword;

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public async Task<SqlConnection> CreateConnectionAsync(string database = "master")
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = database,
            TrustServerCertificate = true
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
