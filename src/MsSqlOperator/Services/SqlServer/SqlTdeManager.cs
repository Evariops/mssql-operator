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

    public async Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        var certPath = $"/tmp/{certName}_{Guid.NewGuid():N}.cer";
        var keyPath = $"/tmp/{certName}_{Guid.NewGuid():N}.pvk";
        var tempPassword = Guid.NewGuid().ToString("N");

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            BACKUP CERTIFICATE [{certName}]
            TO FILE = '{certPath}'
            WITH PRIVATE KEY (FILE = '{keyPath}', ENCRYPTION BY PASSWORD = @password)
            """;
        cmd.Parameters.AddWithValue("@password", tempPassword);
        await cmd.ExecuteNonQueryAsync(ct);

        var certBytes = await File.ReadAllBytesAsync(certPath, ct);
        var keyBytes = await File.ReadAllBytesAsync(keyPath, ct);

        File.Delete(certPath);
        File.Delete(keyPath);

        return (certBytes, keyBytes);
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

    public async Task RestoreCertificateAsync(SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, CancellationToken ct = default)
    {
        var certPath = $"/tmp/{certName}_{Guid.NewGuid():N}.cer";
        var keyPath = $"/tmp/{certName}_{Guid.NewGuid():N}.pvk";
        var tempPassword = Guid.NewGuid().ToString("N");

        await File.WriteAllBytesAsync(certPath, certBytes, ct);
        await File.WriteAllBytesAsync(keyPath, keyBytes, ct);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE CERTIFICATE [{certName}]
                FROM FILE = '{certPath}'
                WITH PRIVATE KEY (FILE = '{keyPath}', DECRYPTION BY PASSWORD = @password)
                """;
            cmd.Parameters.AddWithValue("@password", tempPassword);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            File.Delete(certPath);
            File.Delete(keyPath);
        }
    }

    public async Task DropCertificateAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP CERTIFICATE [{certName}]";
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
            USE [{database}];
            ALTER DATABASE ENCRYPTION KEY
            ENCRYPTION BY SERVER CERTIFICATE [{newCertName}];
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RegenerateDekAsync(SqlConnection connection, string database, string algorithm, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE [{database}];
            ALTER DATABASE ENCRYPTION KEY
            REGENERATE WITH ALGORITHM = {algorithm};
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateWithPasswordAsync(SqlConnection connection, string certName, string encryptionPassword, CancellationToken ct = default)
    {
        var certPath = $"/tmp/{certName}_{Guid.NewGuid():N}.cer";
        var keyPath = $"/tmp/{certName}_{Guid.NewGuid():N}.pvk";

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                BACKUP CERTIFICATE [{certName}]
                TO FILE = '{certPath}'
                WITH PRIVATE KEY (FILE = '{keyPath}', ENCRYPTION BY PASSWORD = @password)
                """;
            cmd.Parameters.AddWithValue("@password", encryptionPassword);
            await cmd.ExecuteNonQueryAsync(ct);

            var certBytes = await File.ReadAllBytesAsync(certPath, ct);
            var keyBytes = await File.ReadAllBytesAsync(keyPath, ct);
            return (certBytes, keyBytes);
        }
        finally
        {
            File.Delete(certPath);
            File.Delete(keyPath);
        }
    }

    public async Task RestoreCertificateWithPasswordAsync(SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct = default)
    {
        var certPath = $"/tmp/{certName}_{Guid.NewGuid():N}.cer";
        var keyPath = $"/tmp/{certName}_{Guid.NewGuid():N}.pvk";

        await File.WriteAllBytesAsync(certPath, certBytes, ct);
        await File.WriteAllBytesAsync(keyPath, keyBytes, ct);

        try
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
        finally
        {
            File.Delete(certPath);
            File.Delete(keyPath);
        }
    }
}
