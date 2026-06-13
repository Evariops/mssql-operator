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
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default, string user = "sa");

    /// <summary>
    /// Short-timeout, no-retry connection for health probes. The default profile
    /// (30s timeout x retries) made a single unreachable replica cost minutes per health pass
    /// — during the very outage where speed matters most.
    /// </summary>
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateProbeConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default);

    string BuildConnectionString(string host, int port, string password, string database = "master", string? applicationIntent = null, string user = "sa");
}

public class SqlConnectionFactory(IOptions<SqlConnectionOptions> options, ILogger<SqlConnectionFactory>? logger = null) : ISqlConnectionFactory
{
    private int _postureWarned;

    public string BuildConnectionString(string host, int port, string password, string database = "master", string? applicationIntent = null, string user = "sa")
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
                "with a trusted SqlConnection:ServerCertificate to authenticate.");
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

    public async Task<Microsoft.Data.SqlClient.SqlConnection> CreateProbeConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(BuildConnectionString(host, port, password, database))
        {
            ConnectTimeout = 3,
            ConnectRetryCount = 0,
        };

        var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default, string user = "sa")
    {
        var connectionString = BuildConnectionString(host, port, password, database, user: user);
        var opts = options.Value;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(ct);
                return connection;
            }
            catch (Exception ex) when (attempt <= opts.OpenRetryCount
                                       && !ct.IsCancellationRequested
                                       && TransientSqlErrors.IsTransient(ex))
            {
                // The pooled connections to this endpoint may all be dead (failover/restart):
                // discard them so the next attempt opens a fresh socket instead of a stale one.
                SqlConnection.ClearPool(connection);
                await connection.DisposeAsync();

                var delay = TimeSpan.FromMilliseconds(opts.OpenRetryBaseDelayMs * Math.Pow(2, attempt - 1));
                logger?.LogWarning(ex,
                    "Transient error opening connection to {Host}:{Port} (attempt {Attempt}/{Max}); retrying in {Delay}ms",
                    host, port, attempt, opts.OpenRetryCount + 1, delay.TotalMilliseconds);
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
