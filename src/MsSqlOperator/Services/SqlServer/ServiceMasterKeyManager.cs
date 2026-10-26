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
            ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(encryptionPassword)}
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTime?> GetSmkModifyDateAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT modify_date FROM sys.symmetric_keys WHERE name = '##MS_ServiceMasterKey##'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime d ? d : null;
    }

}
