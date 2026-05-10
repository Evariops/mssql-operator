using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class ServiceMasterKeyManager : IServiceMasterKeyManager
{
    public async Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            BACKUP SERVICE MASTER KEY TO FILE = '{backupPath}'
            ENCRYPTION BY PASSWORD = '{encryptionPassword}'
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            RESTORE SERVICE MASTER KEY FROM FILE = '{backupPath}'
            DECRYPTION BY PASSWORD = '{decryptionPassword}'
            FORCE
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
