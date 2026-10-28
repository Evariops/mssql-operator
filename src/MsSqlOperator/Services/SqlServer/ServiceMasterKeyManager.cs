using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class ServiceMasterKeyManager : IServiceMasterKeyManager
{
    public async Task<byte[]?> BackupSmkAsync(SqlConnection connection, string encryptionPassword, CancellationToken ct = default)
    {
        // Unguessable, shape-validated temp path. The file stays in the pod's ephemeral
        // /tmp: SQL Server on Linux has no native command to delete an arbitrary file (xp_cmdshell
        // is unavailable), so cleanup is left to the pod lifecycle.
        var backupPath = SqlTempPaths.Create("bak");
        await using (var cmd = connection.CreateCommand())
        {
            // PASSWORD must be an escaped string literal; a query parameter is rejected by
            // the BACKUP/RESTORE SERVICE MASTER KEY syntax.
            cmd.CommandText = $"""
                BACKUP SERVICE MASTER KEY TO FILE = {SqlQuoting.QuoteStringLiteral(backupPath)}
                ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(encryptionPassword)}
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return await SqlTempPaths.ReadAsync(connection, backupPath, ct);
    }

    public async Task<DateTime?> GetSmkModifyDateAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT modify_date FROM sys.symmetric_keys WHERE name = '##MS_ServiceMasterKey##'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime d ? d : null;
    }

}
