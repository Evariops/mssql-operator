using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class ServiceMasterKeyManager : IServiceMasterKeyManager
{
    public async Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // PASSWORD must be an escaped string literal; a query parameter is rejected by
        // the BACKUP/RESTORE SERVICE MASTER KEY syntax.
        cmd.CommandText = $"""
            BACKUP SERVICE MASTER KEY TO FILE = {SqlQuoting.QuoteStringLiteral(backupPath)}
            ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteStringLiteral(encryptionPassword)}
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            RESTORE SERVICE MASTER KEY FROM FILE = {SqlQuoting.QuoteStringLiteral(backupPath)}
            DECRYPTION BY PASSWORD = {SqlQuoting.QuoteStringLiteral(decryptionPassword)}
            FORCE
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
