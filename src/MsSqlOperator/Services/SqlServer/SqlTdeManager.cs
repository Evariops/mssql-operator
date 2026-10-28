using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlTdeManager : ISqlTdeManager
{
    public async Task<bool> MasterKeyExistsAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM master.sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateMasterKeyAsync(SqlConnection connection, string password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // PASSWORD in this DDL clause must be an escaped string literal; a query parameter
        // fails with "Incorrect syntax near '@password'".
        cmd.CommandText = $"CREATE MASTER KEY ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(password)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task OpenMasterKeyAsync(SqlConnection connection, string password, CancellationToken ct = default)
    {
        // Importing/creating a certificate's private key re-encrypts it with the database
        // master key, which must be OPEN in the session. A DMK created in a prior session is
        // not reliably auto-opened for this on SQL Server for Linux, so open it explicitly.
        // Opening an already-open key is a harmless no-op.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"OPEN MASTER KEY DECRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(password)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> CertificateExistsAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM master.sys.certificates WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", certName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateCertificateAsync(SqlConnection connection, string certName, string subject, string? expiryDate, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        var expiryClause = expiryDate != null ? $", EXPIRY_DATE = '{SqlQuoting.ValidateDateLiteral(expiryDate)}'" : "";
        // SUBJECT in CREATE CERTIFICATE must be an escaped string literal, not a parameter.
        cmd.CommandText = $"CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)} WITH SUBJECT = {SqlQuoting.QuoteNStringLiteral(subject)}{expiryClause}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetCertificateThumbprintAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(VARCHAR(512), thumbprint, 1) FROM master.sys.certificates WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", certName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task EnableDatabaseEncryptionAsync(SqlConnection connection, string database, string certName, string algorithm, CancellationToken ct = default)
    {
        await using var cmdKey = connection.CreateCommand();
        cmdKey.CommandText = $"""
            USE {SqlQuoting.QuoteIdentifier(database)};
            IF NOT EXISTS (SELECT 1 FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID())
            BEGIN
                CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {SqlQuoting.ValidateTdeAlgorithm(algorithm)}
                ENCRYPTION BY SERVER CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)};
            END
            """;
        await cmdKey.ExecuteNonQueryAsync(ct);

        await using var cmdEnable = connection.CreateCommand();
        cmdEnable.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET ENCRYPTION ON";
        await cmdEnable.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, string>> GetDatabaseEncryptionStatesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        // The DMV is server-wide — one read serves every database.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT DB_NAME(database_id), {EncryptionStateCase} FROM sys.dm_database_encryption_keys WHERE DB_NAME(database_id) IS NOT NULL";
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            states[reader.GetString(0)] = reader.GetString(1);
        return states;
    }

    private const string EncryptionStateCase = """
        CASE encryption_state
            WHEN 0 THEN 'NotEncrypted'
            WHEN 1 THEN 'Unencrypted'
            WHEN 2 THEN 'EncryptionInProgress'
            WHEN 3 THEN 'Encrypted'
            WHEN 4 THEN 'KeyChangeInProgress'
            WHEN 5 THEN 'DecryptionInProgress'
            WHEN 6 THEN 'ProtectionChangeInProgress'
            ELSE 'Unknown'
        END
        """;

    public async Task<string?> GetDatabaseEncryptionStateAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {EncryptionStateCase} FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID(@db)";
        cmd.Parameters.AddWithValue("@db", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<List<string>> GetUserDatabaseNamesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<string?> GetDekProtectorThumbprintAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CONVERT(VARCHAR(512), dek.encryptor_thumbprint, 1)
            FROM sys.dm_database_encryption_keys dek
            WHERE dek.database_id = DB_ID(@db)
            """;
        cmd.Parameters.AddWithValue("@db", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task ChangeDekProtectorAsync(SqlConnection connection, string database, string newCertName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE {SqlQuoting.QuoteIdentifier(database)};
            ALTER DATABASE ENCRYPTION KEY
            ENCRYPTION BY SERVER CERTIFICATE {SqlQuoting.QuoteIdentifier(newCertName)};
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RegenerateDekAsync(SqlConnection connection, string database, string algorithm, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE {SqlQuoting.QuoteIdentifier(database)};
            ALTER DATABASE ENCRYPTION KEY
            REGENERATE WITH ALGORITHM = {SqlQuoting.ValidateTdeAlgorithm(algorithm)};
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The certificate (DER) and its private key encrypted by
    /// <paramref name="encryptionPassword"/>, returned by the engine itself — CERTENCODED and
    /// CERTPRIVATEKEY. BACKUP CERTIFICATE wrote them to files in the pod, never deleted (SQL Server on
    /// Linux has no command to delete an arbitrary file). The private key is protected by the DMK,
    /// which the caller has opened in the session.
    /// </summary>
    public async Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateWithPasswordAsync(SqlConnection connection, string certName, string encryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CERTENCODED(CERT_ID(@name)), CERTPRIVATEKEY(CERT_ID(@name), @password)";
        cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = certName });
        cmd.Parameters.Add(new SqlParameter("@password", System.Data.SqlDbType.NVarChar, 128) { Value = encryptionPassword });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            throw new InvalidOperationException($"Certificate '{certName}' does not exist: nothing to export.");
        if (reader.IsDBNull(1))
            throw new InvalidOperationException(
                $"The private key of certificate '{certName}' could not be exported: it has none, or the database master key is not open.");
        return ((byte[])reader.GetValue(0), (byte[])reader.GetValue(1));
    }

    public const string DekRegenerationDueProperty = "mssqlop.dekRegenerationDue";

    public async Task<bool> IsDekRegenerationDueAsync(SqlConnection connection, string database, CancellationToken ct = default)
        => await DatabaseProperties.ReadAsync(connection, database, DekRegenerationDueProperty, ct) is not null;

    public Task SetDekRegenerationDueAsync(SqlConnection connection, string database, bool due, CancellationToken ct = default)
        => DatabaseProperties.WriteAsync(connection, database, DekRegenerationDueProperty,
            due ? DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null, ct);

    public Task RestoreCertificateWithPasswordAsync(SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct = default)
        => CreateCertificateFromBinaryAsync(connection, certName, certBytes, keyBytes, decryptionPassword, ct);

    // ── Native certificate import (no filesystem involved) ──
    //
    // SQL Server can create a certificate directly from in-memory binary blobs, so the operator
    // never has to write the .cer/.pvk to the SQL pod — nor read them back (the export is
    // file-free too, see BackupCertificateWithPasswordAsync).
    private static async Task CreateCertificateFromBinaryAsync(SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        // CREATE CERTIFICATE FROM BINARY requires binary *constants* (0x... literals), not
        // query parameters — "Incorrect syntax near '@cert'". The bytes are operator-generated
        // certificate material, and a hex literal cannot carry an injection, so inline them.
        // The DECRYPTION password is likewise a literal clause.
        cmd.CommandText = $"""
            CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}
            FROM BINARY = 0x{Convert.ToHexString(certBytes)}
            WITH PRIVATE KEY (BINARY = 0x{Convert.ToHexString(keyBytes)}, DECRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(decryptionPassword)})
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
