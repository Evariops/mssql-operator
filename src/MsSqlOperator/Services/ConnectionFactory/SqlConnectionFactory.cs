using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.ConnectionFactory;

public interface ISqlConnectionFactory
{
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default);
    string BuildConnectionString(string host, int port, string password, string database = "master");
}

public class SqlConnectionFactory : ISqlConnectionFactory
{
    public string BuildConnectionString(string host, int port, string password, string database = "master")
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = "sa",
            Password = password,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            Encrypt = SqlConnectionEncryptOption.Optional
        };
        return builder.ConnectionString;
    }

    public async Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default)
    {
        var connectionString = BuildConnectionString(host, port, password, database);
        var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
