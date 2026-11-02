using Microsoft.Data.SqlClient;
using MsSqlOperator.Configuration;
using Testcontainers.MsSql;

namespace MsSqlOperator.IntegrationTests;

/// <summary>
/// One shared SQL Server container for the whole integration suite. Declared as an
/// xUnit collection fixture so the 6 test classes share a single container instead of starting
/// 6 sequential ones. The image is the operator's own default for 2022, read from
/// <c>SqlServerImages</c> — so the suite exercises exactly what an unpinned instance would run,
/// and a default bump needs no second edit here.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    private static readonly string Image = SqlServerImages.DefaultImageFor("2022");

    private MsSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();
    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(1433);
    public string Password => MsSqlBuilder.DefaultPassword;

    public async ValueTask InitializeAsync()
    {
        _container = new MsSqlBuilder(Image).Build();
        try
        {
            await _container.StartAsync();
        }
        catch (Exception e)
        {
            // A clear message (instead of an opaque Docker error) when the engine is
            // not available — these tests require Docker.
            throw new InvalidOperationException(
                "Integration tests require a running Docker engine to start the SQL Server Testcontainer. " +
                $"Could not start '{Image}': {e.Message}", e);
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Runs a command inside the SQL Server container, as a pod hook would.</summary>
    public Task<DotNet.Testcontainers.Containers.ExecResult> ExecAsync(IList<string> command)
        => _container.ExecAsync(command);

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

/// <summary>The shared-container collection: every integration test class joins it.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
