using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Services.S3;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>Raised when a forced SMK restore cannot proceed safely: the way out is
/// to repair the safety-backup chain (S3), never to sacrifice the current key.</summary>
public class SmkRestoreBlockedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public interface ISmkRestoreOrchestrator
{
    /// <summary>
    /// Restores a service master key with the invariant: a FORCE can never make data
    /// irrecoverable. Plain restore is attempted first; on a decryption failure the CURRENT
    /// SMK is backed up to a timestamped, never-overwritten S3 key and verified by re-reading
    /// it, and only then is FORCE issued. Returns the S3 key of the safety backup (null when
    /// the plain restore succeeded).
    /// </summary>
    Task<string?> RestoreAsync(
        SqlConnection connection,
        string instanceName,
        string restoreFilePath,
        string restoreDecryptionPassword,
        S3ConnectionInfo safetyStore,
        string safetyBackupPassword,
        CancellationToken ct = default);
}

public class SmkRestoreOrchestrator(
    ILogger<SmkRestoreOrchestrator> logger,
    IServiceMasterKeyManager smkManager,
    IS3Client s3Client) : ISmkRestoreOrchestrator
{
    public async Task<string?> RestoreAsync(
        SqlConnection connection,
        string instanceName,
        string restoreFilePath,
        string restoreDecryptionPassword,
        S3ConnectionInfo safetyStore,
        string safetyBackupPassword,
        CancellationToken ct = default)
    {
        try
        {
            await smkManager.RestoreSmkAsync(connection, restoreFilePath, restoreDecryptionPassword, ct);
            return null; // clean restore — nothing was at risk
        }
        catch (SqlException ex)
        {
            logger.LogWarning(ex,
                "Plain SMK restore failed on {Instance}; preparing reversible FORCE (current SMK will be safety-backed-up first)",
                instanceName);
        }

        // 1. Export the CURRENT SMK (the one FORCE would replace) into the pod and read it back.
        var tempPath = SqlTempPaths.Create("bak");
        byte[] currentSmk;
        try
        {
            await smkManager.BackupSmkAsync(connection, tempPath, safetyBackupPassword, ct);
            currentSmk = await ReadFileViaSqlAsync(connection, tempPath, ct);
        }
        catch (Exception ex)
        {
            throw new SmkRestoreBlockedException(
                $"Cannot back up the current service master key of {instanceName}; refusing to FORCE-restore. " +
                "Repair the backup path, do not sacrifice the current key.", ex);
        }

        // 2. Timestamped, write-once S3 key — never overwritten, never deleted by the operator.
        var objectKey = $"smk/{instanceName}/{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}.bak";
        if (await s3Client.ObjectExistsAsync(safetyStore, objectKey, ct))
            throw new SmkRestoreBlockedException($"Safety key {objectKey} already exists; refusing to overwrite.");

        try
        {
            await s3Client.PutObjectAsync(safetyStore, objectKey, currentSmk, ct);

            // 3. Verify by re-reading: the FORCE is only reversible if this copy is readable.
            var readBack = await s3Client.GetObjectAsync(safetyStore, objectKey, ct);
            if (!readBack.AsSpan().SequenceEqual(currentSmk))
                throw new SmkRestoreBlockedException($"Safety backup {objectKey} read back differently than written.");
        }
        catch (SmkRestoreBlockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SmkRestoreBlockedException(
                $"Safety backup of the current SMK to S3 ({objectKey}) could not be written and verified; refusing to FORCE-restore.", ex);
        }

        // 4. The current key is now recoverable — the FORCE cannot destroy anything for good.
        logger.LogWarning(
            "FORCE-restoring service master key on {Instance}. The previous SMK is recoverable from S3 key {Key} " +
            "(decryption password: the configured SMK backup password Secret).",
            instanceName, objectKey);
        await smkManager.RestoreSmkForcedAsync(connection, restoreFilePath, restoreDecryptionPassword, ct);
        return objectKey;
    }

    private static async Task<byte[]> ReadFileViaSqlAsync(SqlConnection connection, string filePath, CancellationToken ct)
    {
        SqlTempPaths.EnsureSafe(filePath);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[]
            ?? throw new InvalidOperationException($"Could not read back {filePath} via OPENROWSET.");
    }
}
