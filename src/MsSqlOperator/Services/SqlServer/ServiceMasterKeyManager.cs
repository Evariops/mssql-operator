using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.SqlServer;

public class ServiceMasterKeyManager(ILogger<ServiceMasterKeyManager>? logger = null) : IServiceMasterKeyManager
{
    public async Task<byte[]?> BackupSmkAsync(SqlConnection connection, string encryptionPassword, bool keyVolumeMounted,
        CancellationToken ct = default)
    {
        // Unguessable, shape-validated temp path. SQL Server on Linux has no native
        // command to delete an arbitrary file (xp_cmdshell is unavailable), so the file stays until
        // the pod goes — in the pod's memory-backed volume, never on the node's disk.
        var (backupPath, inMemory) = await SqlTempPaths.CreateForKeyAsync(connection, "bak", keyVolumeMounted, ct);
        if (!inMemory)
            logger?.LogWarning("{Server} has no in-memory key volume (pod created by an earlier operator version): the SMK " +
                               "backup file goes to /tmp until the pod is recreated", connection.DataSource);
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
