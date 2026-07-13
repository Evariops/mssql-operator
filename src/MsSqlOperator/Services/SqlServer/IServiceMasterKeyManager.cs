using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public interface IServiceMasterKeyManager
{
    Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default);

    /// <summary>
    /// When the Service Master Key was last changed. The SMK backup is taken once and
    /// then never revisited: if the key is regenerated afterwards — a machine-key change, a restore,
    /// <c>ALTER SERVICE MASTER KEY REGENERATE</c> — the stored backup silently stops matching, and the
    /// one artifact that would save the encrypted data is quietly worthless. Comparing this date with
    /// the date of the stored backup is what makes the staleness visible.
    /// </summary>
    Task<DateTime?> GetSmkModifyDateAsync(SqlConnection connection, CancellationToken ct = default);

    /// <summary>Plain restore — never FORCE.</summary>
    Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default);

    /// <summary>FORCE restore. Only <see cref="SmkRestoreOrchestrator"/> may call this, after
    /// the current SMK has been safety-backed-up and verified.</summary>
    Task RestoreSmkForcedAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default);
}
