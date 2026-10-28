using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public interface IServiceMasterKeyManager
{
    /// <summary>Backs the Service Master Key up to a temp file of the server and reads it back
    /// through SQL; null when the file came back empty.</summary>
    Task<byte[]?> BackupSmkAsync(SqlConnection connection, string encryptionPassword, CancellationToken ct = default);

    /// <summary>
    /// When the Service Master Key was last changed. The SMK backup is taken once and
    /// then never revisited: if the key is regenerated afterwards — a machine-key change, a restore,
    /// <c>ALTER SERVICE MASTER KEY REGENERATE</c> — the stored backup silently stops matching, and the
    /// one artifact that would save the encrypted data is quietly worthless. Comparing this date with
    /// the date of the stored backup is what makes the staleness visible.
    /// </summary>
    Task<DateTime?> GetSmkModifyDateAsync(SqlConnection connection, CancellationToken ct = default);

}
