using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlConnectionFactory"/>: returns a throwaway
/// <see cref="SqlConnection"/> per call (overridable) and records the connection parameters.</summary>
public sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
{
    public readonly List<(string Host, int Port, string Password, string Database, string User)> Calls = [];

    /// <summary>Produce the connection per call, keyed by host. The default carries the host in
    /// <c>DataSource</c> so per-connection fakes (roles, demotion) can tell replicas apart — the
    /// failover protocol demotes one replica and promotes another in the same pass, so they MUST be
    /// distinguishable. Throw inside it to simulate an unreachable host.</summary>
    public Func<string, SqlConnection> ConnectionForHost { get; set; } = h => new SqlConnection($"Server={h}");

    public Task<SqlConnection> CreateConnectionAsync(string host, int port, string password,
        string database = "master", CancellationToken ct = default, string user = "sa", string caller = "")
    {
        Calls.Add((host, port, password, database, user));
        return Task.FromResult(ConnectionForHost(host));
    }

    /// <summary>The profile of each <see cref="OpenAsync"/> call.</summary>
    public readonly List<(string Host, SqlProfile Profile)> ProfileCalls = [];

    public Task<SqlConnection> OpenAsync(string host, int port, string password, SqlProfile profile,
        string database = "master", CancellationToken ct = default, string user = "sa", string caller = "")
    {
        ProfileCalls.Add((host, profile));
        return profile == SqlProfile.Probe
            ? CreateProbeConnectionAsync(host, port, password, database, ct)
            : CreateConnectionAsync(host, port, password, database, ct, user);
    }

    public readonly List<(string Host, string? HostNameInCertificate)> ProbeCalls = [];

    /// <summary>Hosts whose probe never completes and ignores its cancellation — a frozen server
    /// holding its TCP session open.</summary>
    public Func<string, bool> ProbeHangs { get; set; } = _ => false;

    public Task<SqlConnection> CreateProbeConnectionAsync(string host, int port, string password,
        string database = "master", CancellationToken ct = default, string? hostNameInCertificate = null, string caller = "")
    {
        Calls.Add((host, port, password, database, "sa"));
        ProbeCalls.Add((host, hostNameInCertificate));
        if (ProbeHangs(host))
            return new TaskCompletionSource<SqlConnection>().Task;
        return Task.FromResult(ConnectionForHost(host));
    }

    public string BuildConnectionString(string host, int port, string password,
        string database = "master", string? applicationIntent = null, string user = "sa", string? component = null)
        => $"Server={host},{port};Database={database};User Id={user};";
}
