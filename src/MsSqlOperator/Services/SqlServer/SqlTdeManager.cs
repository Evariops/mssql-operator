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

    public async Task DisableDatabaseEncryptionAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET ENCRYPTION OFF";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetDatabaseEncryptionStateAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CASE encryption_state
                WHEN 0 THEN 'NotEncrypted'
                WHEN 1 THEN 'Unencrypted'
                WHEN 2 THEN 'EncryptionInProgress'
                WHEN 3 THEN 'Encrypted'
                WHEN 4 THEN 'KeyChangeInProgress'
                WHEN 5 THEN 'DecryptionInProgress'
                WHEN 6 THEN 'ProtectionChangeInProgress'
                ELSE 'Unknown'
            END
            FROM sys.dm_database_encryption_keys
            WHERE database_id = DB_ID(@db)
            """;
        cmd.Parameters.AddWithValue("@db", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task BackupCertificateAsync(SqlConnection connection, string certName, string certPath, string keyPath, string encryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            BACKUP CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}
            TO FILE = {SqlQuoting.QuoteStringLiteral(certPath)}
            WITH PRIVATE KEY (FILE = {SqlQuoting.QuoteStringLiteral(keyPath)}, ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(encryptionPassword)})
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropCertificateAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}";
        await cmd.ExecuteNonQueryAsync(ct);
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

    public async Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateWithPasswordAsync(SqlConnection connection, string certName, string encryptionPassword, CancellationToken ct = default)
    {
        var certPath = SafeTempPath("cer");
        var keyPath = SafeTempPath("pvk");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            BACKUP CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}
            TO FILE = {SqlQuoting.QuoteStringLiteral(certPath)}
            WITH PRIVATE KEY (FILE = {SqlQuoting.QuoteStringLiteral(keyPath)}, ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(encryptionPassword)})
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        var certBytes = await ReadFileViaSqlAsync(connection, certPath, ct);
        var keyBytes = await ReadFileViaSqlAsync(connection, keyPath, ct);
        return (certBytes, keyBytes);
    }

    public Task RestoreCertificateWithPasswordAsync(SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct = default)
        => CreateCertificateFromBinaryAsync(connection, certName, certBytes, keyBytes, decryptionPassword, ct);

    // ── Native certificate import (no filesystem involved) ──
    //
    // SQL Server can create a certificate directly from in-memory binary blobs, so the operator
    // never has to write the .cer/.pvk to the SQL pod. This is the only direction that has a
    // fully native, file-free command. (Export of a private key has no inline equivalent — see
    // BackupCertificate* below — BACKUP CERTIFICATE only writes to a file.)
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

    // ── Certificate export read-back ──
    //
    // BACKUP CERTIFICATE is the only way SQL Server exports a private key, and it writes to the
    // SQL pod's own filesystem. We read the bytes straight back with OPENROWSET (a native,
    // Linux-compatible query — no xp_cmdshell). The temp file stays in the pod's ephemeral /tmp
    // under a unique name; SQL Server has no native command to delete an arbitrary file, so we
    // rely on the pod lifecycle rather than xp_cmdshell (unavailable on SQL Server for Linux).

    private static string SafeTempPath(string extension) => $"/tmp/mssqlop_{Guid.NewGuid():N}.{extension}";

    private static void EnsureSafeTempPath(string filePath)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(filePath, @"^/tmp/mssqlop_[0-9a-f]{32}\.[a-z0-9]+$"))
            throw new ArgumentException($"Refusing to use unsafe SQL file path '{filePath}'.", nameof(filePath));
    }

    private static async Task<byte[]> ReadFileViaSqlAsync(SqlConnection connection, string filePath, CancellationToken ct)
    {
        EnsureSafeTempPath(filePath);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[] ?? throw new InvalidOperationException($"Failed to read file '{filePath}' via SQL Server OPENROWSET.");
    }
}
