using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public interface IServiceMasterKeyManager
{
    Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default);

    /// <summary>Plain restore — never FORCE.</summary>
    Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default);

    /// <summary>FORCE restore. Only <see cref="SmkRestoreOrchestrator"/> may call this, after
    /// the current SMK has been safety-backed-up and verified.</summary>
    Task RestoreSmkForcedAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default);
}
