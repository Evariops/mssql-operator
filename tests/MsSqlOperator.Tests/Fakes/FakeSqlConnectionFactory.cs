using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlConnectionFactory"/>: returns a throwaway
/// <see cref="SqlConnection"/> per call (overridable) and records the connection parameters.</summary>
public sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
{
    public readonly List<(string Host, int Port, string Password, string Database, string User)> Calls = [];

    /// <summary>Override the connection produced per call (default: a fresh, unopened SqlConnection).</summary>
    public Func<SqlConnection> ConnectionFactory { get; set; } = () => new SqlConnection();

    public Task<SqlConnection> CreateConnectionAsync(string host, int port, string password,
        string database = "master", CancellationToken ct = default, string user = "sa")
    {
        Calls.Add((host, port, password, database, user));
        return Task.FromResult(ConnectionFactory());
    }

    public Task<SqlConnection> CreateProbeConnectionAsync(string host, int port, string password,
        string database = "master", CancellationToken ct = default)
    {
        Calls.Add((host, port, password, database, "sa"));
        return Task.FromResult(ConnectionFactory());
    }

    public string BuildConnectionString(string host, int port, string password,
        string database = "master", string? applicationIntent = null, string user = "sa")
        => $"Server={host},{port};Database={database};User Id={user};";
}
