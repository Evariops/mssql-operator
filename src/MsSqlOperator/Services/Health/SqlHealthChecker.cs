using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.Services.Health;

public interface ISqlHealthChecker
{
    Task<bool> IsHealthyAsync(string host, int port, string password, CancellationToken ct = default);
    Task<bool> IsHealthyAsync(SqlConnection connection, CancellationToken ct = default);
}

public class SqlHealthChecker(ISqlConnectionFactory connectionFactory) : ISqlHealthChecker
{
    public async Task<bool> IsHealthyAsync(string host, int port, string password, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);
            return await IsHealthyAsync(connection, ct);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsHealthyAsync(SqlConnection connection, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result) == 1;
        }
        catch
        {
            return false;
        }
    }
}
