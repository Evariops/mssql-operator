using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public interface IServiceMasterKeyManager
{
    Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default);
    Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default);
}
