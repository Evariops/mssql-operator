using System.Text.RegularExpressions;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Temp-file paths used for SQL-side export/read-back (BACKUP CERTIFICATE / SERVICE MASTER KEY
/// + OPENROWSET). Generated under /tmp with a unique unguessable name; the strict shape check
/// is defense in depth before the path is embedded (quoted) into SQL text.
/// </summary>
public static partial class SqlTempPaths
{
    [GeneratedRegex(@"^/tmp/mssqlop_[0-9a-f]{32}\.[a-z0-9]+$")]
    private static partial Regex SafeShape();

    public static string Create(string extension) => $"/tmp/mssqlop_{Guid.NewGuid():N}.{extension}";

    public static string EnsureSafe(string filePath)
    {
        if (!SafeShape().IsMatch(filePath))
            throw new ArgumentException($"Refusing to use unsafe SQL file path '{filePath}'.", nameof(filePath));
        return filePath;
    }

    /// <summary>Reads back, through SQL Server (OPENROWSET … SINGLE_BLOB), a file the engine wrote
    /// under a <see cref="Create"/> path; null when it is empty. One implementation instead
    /// of three.</summary>
    public static async Task<byte[]?> ReadAsync(Microsoft.Data.SqlClient.SqlConnection connection, string filePath, CancellationToken ct)
    {
        EnsureSafe(filePath);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        return await cmd.ExecuteScalarAsync(ct) as byte[] is { Length: > 0 } bytes ? bytes : null;
    }
}
