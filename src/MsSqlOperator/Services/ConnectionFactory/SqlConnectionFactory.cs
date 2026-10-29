using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MsSqlOperator.Services.ConnectionFactory;

public class SqlConnectionOptions
{
    /// <summary>Optional | Mandatory | Strict. Defaults to Mandatory (encrypted, but server cert trusted — see TrustServerCertificate).</summary>
    public string Encrypt { get; set; } = "Mandatory";

    /// <summary>
    /// When true, the server certificate is not validated against a CA. Suitable for the default
    /// in-cluster self-signed pod certificates. Set to false for full chain validation, in which
    /// case provide HostNameInCertificate and/or ServerCertificate.
    /// </summary>
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Expected name in the server certificate (SAN/CN) when it differs from DataSource — required for validation across pod FQDNs.</summary>
    public string? HostNameInCertificate { get; set; }

    /// <summary>Path to a PEM/DER certificate file to validate the server certificate against (Encrypt=Strict / TDS 8.0).</summary>
    public string? ServerCertificate { get; set; }

    public int ConnectTimeout { get; set; } = 30;

    /// <summary>Driver-level reconnect attempts on an idle/broken connection (ConnectRetryCount).</summary>
    public int ConnectRetryCount { get; set; } = 3;

    /// <summary>Seconds between driver-level reconnect attempts (ConnectRetryInterval).</summary>
    public int ConnectRetryInterval { get; set; } = 5;

    /// <summary>Application-level retries when opening a connection fails with a transient error
    /// (e.g. database unavailable mid-failover). Distinct from the driver's idle-connection resiliency.</summary>
    public int OpenRetryCount { get; set; } = 4;

    /// <summary>Base delay (ms) for the exponential backoff between open retries.</summary>
    public int OpenRetryBaseDelayMs { get; set; } = 500;
}

public interface ISqlConnectionFactory
{
    /// <summary>
    /// Opens a connection. <paramref name="user"/> defaults to <c>sa</c>; the deploy path passes the
    /// least-privilege stored-procedure deployer login.
    /// </summary>
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master",
        CancellationToken ct = default, string user = "sa", [CallerFilePath] string caller = "");

    /// <summary>
    /// Opens a connection with the timeouts, retries and session settings of
    /// <paramref name="profile"/>. Every connection carries <c>Application Name =
    /// mssql-operator/&lt;component&gt;</c> (the calling file when not given), so that DBAs see the
    /// operator in sys.dm_exec_sessions and in blocking chains.
    /// </summary>
    Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(string host, int port, string password, SqlProfile profile,
        string database = "master", CancellationToken ct = default, string user = "sa", [CallerFilePath] string caller = "");

    /// <summary>
    /// Short-timeout, no-retry connection for health probes. The default profile
    /// (30s timeout x retries) made a single unreachable replica cost minutes per health pass
    /// — during the very outage where speed matters most.
    /// </summary>
    /// <remarks><paramref name="hostNameInCertificate"/>: the name to validate the server certificate
    /// against when <paramref name="host"/> is not it (a pod IP). SqlConnection:HostNameInCertificate,
    /// when configured, still wins.</remarks>
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateProbeConnectionAsync(string host, int port, string password, string database = "master",
        CancellationToken ct = default, string? hostNameInCertificate = null, [CallerFilePath] string caller = "");

    string BuildConnectionString(string host, int port, string password, string database = "master", string? applicationIntent = null,
        string user = "sa", string? component = null);
}

public class SqlConnectionFactory(IOptions<SqlConnectionOptions> options, ILogger<SqlConnectionFactory>? logger = null,
    EndpointHealth? endpointHealth = null) : ISqlConnectionFactory
{
    private int _postureWarned;

    /// <summary>The <c>Application Name</c> of the operator's sessions.</summary>
    public const string ApplicationName = "mssql-operator";

    /// <summary>The component part of the application name: the calling file's name.</summary>
    internal static string ComponentOf(string caller)
        => string.IsNullOrEmpty(caller) ? "operator" : Path.GetFileNameWithoutExtension(caller.Replace('\\', '/'));

    public string BuildConnectionString(string host, int port, string password, string database = "master", string? applicationIntent = null,
        string user = "sa", string? component = null)
    {
        var opts = options.Value;
        var encrypt = opts.Encrypt?.ToLowerInvariant() switch
        {
            "optional" or "false" => SqlConnectionEncryptOption.Optional,
            "strict" => SqlConnectionEncryptOption.Strict,
            _ => SqlConnectionEncryptOption.Mandatory
        };

        // Surface the insecure-by-default posture once. Mandatory/Optional + Trust means the
        // connection is encrypted but the SQL server is not authenticated → MITM-able from inside the
        // cluster. Strict authenticates the server, so it is not flagged.
        if (opts.TrustServerCertificate && encrypt != SqlConnectionEncryptOption.Strict
            && Interlocked.Exchange(ref _postureWarned, 1) == 0)
        {
            logger?.LogWarning(
                "SQL Server certificate validation is disabled (TrustServerCertificate=true): operator-to-SQL " +
                "traffic is encrypted but the server is not authenticated, so an on-path attacker inside the " +
                "cluster could MITM the connection and capture credentials. Set SqlConnection:Encrypt=Strict " +
                "with a trusted SqlConnection:ServerCertificate to authenticate. See docs/guides/security.md.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = user,
            Password = password,
            TrustServerCertificate = opts.TrustServerCertificate,
            ConnectTimeout = opts.ConnectTimeout,
            Encrypt = encrypt,
            // Driver-level resiliency: transparently recover an idle connection dropped by a
            // brief transport blip (e.g. a secondary's pod restart) without surfacing an error.
            ConnectRetryCount = opts.ConnectRetryCount,
            ConnectRetryInterval = opts.ConnectRetryInterval,
            ApplicationName = component is null ? ApplicationName : $"{ApplicationName}/{component}",
        };

        // ApplicationIntent=ReadOnly lets the read path follow read-only routing to a readable
        // secondary; write paths must omit it so they always land on the primary.
        if (string.Equals(applicationIntent, "ReadOnly", StringComparison.OrdinalIgnoreCase))
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;

        // Full chain/hostname validation knobs — only applied when configured so the default
        // (self-signed, trusted) path is unaffected.
        if (!string.IsNullOrWhiteSpace(opts.HostNameInCertificate))
            builder.HostNameInCertificate = opts.HostNameInCertificate;
        if (!string.IsNullOrWhiteSpace(opts.ServerCertificate))
            builder.ServerCertificate = opts.ServerCertificate;

        return builder.ConnectionString;
    }

    public Task<Microsoft.Data.SqlClient.SqlConnection> CreateProbeConnectionAsync(string host, int port, string password, string database = "master",
        CancellationToken ct = default, string? hostNameInCertificate = null, [CallerFilePath] string caller = "")
        // Its callers also resume, re-seed and drop over it: the commands keep the 30 s default.
        // OpenAsync(SqlProfile.Probe) is the read that must fail in 5 s.
        => OpenProbeAsync(host, port, password, database, ct, hostNameInCertificate, caller, commandTimeoutSeconds: 30);

    private async Task<Microsoft.Data.SqlClient.SqlConnection> OpenProbeAsync(string host, int port, string password, string database,
        CancellationToken ct, string? hostNameInCertificate, string caller, int commandTimeoutSeconds)
    {
        var probe = SqlProfileSettings.For(SqlProfile.Probe, options.Value);
        var builder = new SqlConnectionStringBuilder(BuildConnectionString(host, port, password, database, component: ComponentOf(caller)))
        {
            ConnectTimeout = probe.ConnectTimeoutSeconds,
            ConnectRetryCount = 0,
            CommandTimeout = commandTimeoutSeconds,
        };
        if (hostNameInCertificate is not null && string.IsNullOrWhiteSpace(options.Value.HostNameInCertificate))
            builder.HostNameInCertificate = hostNameInCertificate;

        var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        // A pooled connection to a frozen server (SIGSTOP: the kernel still holds the TCP
        // session) opens without any round-trip, and its first command then ignores its cancellation —
        // SqlClient waits for an attention acknowledgement that never comes. The AG pass hung 7.5
        // minutes on its frozen primary: no detection, no failover. The connection is checked under a
        // hard deadline; one that does not answer is left to its stuck command and its pool cleared,
        // so that the next probe logs in afresh — bounded by ConnectTimeout, which a frozen server
        // cannot meet. (Unpooled probes were tried: SqlClient serializes asynchronous unpooled opens on
        // one slot per CPU, and the open stuck on the frozen primary timed out a healthy secondary's.)
        var check = PingAsync(connection);
        try
        {
            await check.WaitAsync(ProbeCheckDeadline, ct);
            return connection;
        }
        catch (TimeoutException)
        {
            SqlConnection.ClearPool(connection);
            _ = check.ContinueWith(_ => connection.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            throw new TimeoutException($"{host} accepted the connection but did not answer within {ProbeCheckDeadline.TotalSeconds}s");
        }
        catch
        {
            if (check.IsCompleted)
                await connection.DisposeAsync();
            else
                _ = check.ContinueWith(_ => connection.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>How long a probe connection has to answer a trivial query.</summary>
    internal static readonly TimeSpan ProbeCheckDeadline = TimeSpan.FromSeconds(5);

    private static async Task PingAsync(Microsoft.Data.SqlClient.SqlConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = 3;
        await cmd.ExecuteScalarAsync();
    }

    public Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master",
        CancellationToken ct = default, string user = "sa", [CallerFilePath] string caller = "")
        => OpenAsync(host, port, password, SqlProfile.Default, database, ct, user, caller);

    public async Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(string host, int port, string password, SqlProfile profile,
        string database = "master", CancellationToken ct = default, string user = "sa", [CallerFilePath] string caller = "")
    {
        var opts = options.Value;
        var settings = SqlProfileSettings.For(profile, opts);
        if (profile == SqlProfile.Probe)
            return await OpenProbeAsync(host, port, password, database, ct, null, caller, settings.CommandTimeoutSeconds);
        // An endpoint the last state read could not reach fails at once (Enforce).
        endpointHealth?.Check(host);

        var builder = new SqlConnectionStringBuilder(BuildConnectionString(host, port, password, database, user: user, component: ComponentOf(caller)))
        {
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            CommandTimeout = settings.CommandTimeoutSeconds,
        };
        var connectionString = builder.ConnectionString;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(ct);
                if (settings.SessionBatch is { } session)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = session;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                return connection;
            }
            catch (Exception ex) when (attempt <= settings.OpenRetries
                                       && !ct.IsCancellationRequested
                                       && TransientSqlErrors.IsTransient(ex))
            {
                // The pooled connections to this endpoint may all be dead (failover/restart):
                // discard them so the next attempt opens a fresh socket instead of a stale one.
                SqlConnection.ClearPool(connection);
                await connection.DisposeAsync();

                // Jitter, so that the workers that failed together do not come back together.
                var delay = TimeSpan.FromMilliseconds(opts.OpenRetryBaseDelayMs * Math.Pow(2, attempt - 1)
                                                      * (1 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(500) / 1000.0));
                logger?.LogWarning(ex,
                    "Transient error opening connection to {Host}:{Port} (attempt {Attempt}/{Max}); retrying in {Delay}ms",
                    host, port, attempt, settings.OpenRetries + 1, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
}
