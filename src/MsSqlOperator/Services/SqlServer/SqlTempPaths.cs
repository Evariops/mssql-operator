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
}
