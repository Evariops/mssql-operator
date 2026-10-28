using System.Text.RegularExpressions;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Temp-file paths used for SQL-side export/read-back (BACKUP SERVICE MASTER KEY + OPENROWSET).
/// Generated with a unique unguessable name; the strict shape check is defense in depth before the
/// path is embedded (quoted) into SQL text.
/// </summary>
public static partial class SqlTempPaths
{
    /// <summary>The memory-backed volume of the SQL pods (StatefulSetBuilder).</summary>
    public const string KeyDirectory = "/var/opt/mssqlop-keys";

    [GeneratedRegex(@"^(/tmp|/var/opt/mssqlop-keys)/mssqlop_[0-9a-f]{32}\.[a-z0-9]+$")]
    private static partial Regex SafeShape();

    private static string CreateIn(string directory, string extension) => $"{directory}/mssqlop_{Guid.NewGuid():N}.{extension}";

    /// <summary>
    /// A path for key material, in <see cref="KeyDirectory"/> when the server has it. A pod
    /// created before the volume existed has not: /tmp, as before, until it is recreated — the
    /// second value says which. A pod that has the volume never falls back to the disk;
    /// when its server does not see the directory, the caller retries on a later pass.
    /// </summary>
    public static async Task<(string Path, bool InMemory)> CreateForKeyAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        string extension, bool volumeMounted, CancellationToken ct)
    {
        var inMemory = await IsDirectoryAsync(connection, KeyDirectory, ct);
        if (!inMemory && volumeMounted)
            throw new InvalidOperationException($"{connection.DataSource}: the pod mounts {KeyDirectory} but SQL Server does not see it; " +
                                                "the key backup waits for a later pass rather than going to the node's disk.");
        return (CreateIn(inMemory ? KeyDirectory : "/tmp", extension), inMemory);
    }

    /// <summary>Whether SQL Server sees <paramref name="path"/> as a directory. The
    /// columns of sys.dm_os_file_exists are tinyint, not bit — read as a byte, the answer was never
    /// <c>true</c>, and every key backup went to /tmp.</summary>
    public static async Task<bool> IsDirectoryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string path, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(file_is_a_directory AS bit) FROM sys.dm_os_file_exists(@dir)";
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@dir", System.Data.SqlDbType.NVarChar, 260) { Value = path });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public static string EnsureSafe(string filePath)
    {
        if (!SafeShape().IsMatch(filePath))
            throw new ArgumentException($"Refusing to use unsafe SQL file path '{filePath}'.", nameof(filePath));
        return filePath;
    }

    /// <summary>Reads back, through SQL Server (OPENROWSET … SINGLE_BLOB), a file the engine wrote
    /// under a <see cref="CreateForKeyAsync"/> path; null when it is empty. One implementation instead
    /// of three.</summary>
    public static async Task<byte[]?> ReadAsync(Microsoft.Data.SqlClient.SqlConnection connection, string filePath, CancellationToken ct)
    {
        EnsureSafe(filePath);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        return await cmd.ExecuteScalarAsync(ct) as byte[] is { Length: > 0 } bytes ? bytes : null;
    }
}
