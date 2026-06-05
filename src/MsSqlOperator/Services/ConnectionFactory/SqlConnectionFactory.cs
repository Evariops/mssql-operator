using Microsoft.Data.SqlClient;
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
}

public interface ISqlConnectionFactory
{
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateConnectionAsync(string host, int port, string password, string database = "master", CancellationToken ct = default);
    string BuildConnectionString(string host, int port, string password, string database = "master");
}

public class SqlConnectionFactory(IOptions<SqlConnectionOptions> options) : ISqlConnectionFactory
{
    public string BuildConnectionString(string host, int port, string password, string database = "master")
    {
        var opts = options.Value;
        var encrypt = opts.Encrypt?.ToLowerInvariant() switch
        {
            "optional" or "false" => SqlConnectionEncryptOption.Optional,
            "strict" => SqlConnectionEncryptOption.Strict,
            _ => SqlConnectionEncryptOption.Mandatory
        };

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = "sa",
            Password = password,
            TrustServerCertificate = opts.TrustServerCertificate,
            ConnectTimeout = opts.ConnectTimeout,
            Encrypt = encrypt
        };

        // Full chain/hostname validation knobs — only applied when configured so the default
        // (self-signed, trusted) path is unaffected.
        if (!string.IsNullOrWhiteSpace(opts.HostNameInCertificate))
            builder.HostNameInCertificate = opts.HostNameInCertificate;
        if (!string.IsNullOrWhiteSpace(opts.ServerCertificate))
            builder.ServerCertificate = opts.ServerCertificate;

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
