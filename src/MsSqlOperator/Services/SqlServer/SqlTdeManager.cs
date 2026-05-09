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
        cmd.CommandText = $"CREATE MASTER KEY ENCRYPTION BY PASSWORD = @password";
        cmd.Parameters.AddWithValue("@password", password);
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
        var expiryClause = expiryDate != null ? $", EXPIRY_DATE = '{expiryDate}'" : "";
        cmd.CommandText = $"CREATE CERTIFICATE [{certName}] WITH SUBJECT = @subject{expiryClause}";
        cmd.Parameters.AddWithValue("@subject", subject);
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
            USE [{database}];
            IF NOT EXISTS (SELECT 1 FROM sys.dm_database_encryption_keys WHERE database_id = DB_ID())
            BEGIN
                CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {algorithm}
                ENCRYPTION BY SERVER CERTIFICATE [{certName}];
            END
            """;
        await cmdKey.ExecuteNonQueryAsync(ct);

        await using var cmdEnable = connection.CreateCommand();
        cmdEnable.CommandText = $"ALTER DATABASE [{database}] SET ENCRYPTION ON";
        await cmdEnable.ExecuteNonQueryAsync(ct);
    }

    public async Task DisableDatabaseEncryptionAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{database}] SET ENCRYPTION OFF";
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
            BACKUP CERTIFICATE [{certName}]
            TO FILE = '{certPath}'
            WITH PRIVATE KEY (FILE = '{keyPath}', ENCRYPTION BY PASSWORD = @password)
            """;
        cmd.Parameters.AddWithValue("@password", encryptionPassword);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ImportCertificateAsync(SqlConnection connection, string certName, string certPath, string keyPath, string decryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE CERTIFICATE [{certName}]
            FROM FILE = '{certPath}'
            WITH PRIVATE KEY (FILE = '{keyPath}', DECRYPTION BY PASSWORD = @password)
            """;
        cmd.Parameters.AddWithValue("@password", decryptionPassword);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropCertificateAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP CERTIFICATE [{certName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
