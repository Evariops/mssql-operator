using System.Text.RegularExpressions;

namespace MsSqlOperator.Tests;

/// <summary>
/// Anti-regression guard for raw string interpolation in SQL building code.
/// Scans the sources of Services/SqlServer/ and Services/Kubernetes/MssqlConfBuilder.cs and fails
/// when an interpolation hole appears directly inside a bracketed identifier ("[{") or a string
/// literal ("'{") without going through SqlQuoting. Identifiers and DDL object names cannot be
/// parameterized in T-SQL, so SqlQuoting.QuoteIdentifier / QuoteStringLiteral / QuoteNStringLiteral
/// are the only acceptable defense.
/// </summary>
public class SqlInterpolationGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Matches "[{" or "'{" unless the hole immediately invokes SqlQuoting (validated/quoted inline).</summary>
    private static readonly Regex RawInterpolation = new(@"(\[\{|'\{)(?!SqlQuoting\.)", RegexOptions.Compiled);

    /// <summary>
    /// Lines exempted from the guard, matched by (file name, line substring).
    /// Every entry must justify why it is safe. Entries must stay in use: an
    /// allowlisted pattern that no longer matches any line fails the test too.
    /// </summary>
    private static readonly (string File, string LineContains, string Justification)[] Allowlist =
    [
        // -- Permanently justified: not SQL (operator event / exception messages) --
        ("AgFailoverOrchestrator.cs", "Fencing strategy '{strategy}' requires external/manual action",
            "Kubernetes event message, never sent to SQL Server"),
        ("SqlTdeManager.cs", "Refusing to use unsafe SQL file path '{filePath}'",
            "ArgumentException message, never sent to SQL Server"),
        ("SqlTdeManager.cs", "Failed to read file '{filePath}' via SQL Server OPENROWSET",
            "InvalidOperationException message, never sent to SQL Server"),
        ("SqlIdentifierRules.cs", "is not an accepted SQL identifier",
            "validation error message, never sent to SQL Server"),
    ];

    public static TheoryData<string> ScannedFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in EnumerateScannedFiles())
            data.Add(Path.GetRelativePath(RepoRoot, file));
        return data;
    }

    [Theory]
    [MemberData(nameof(ScannedFiles))]
    public void SqlSources_DoNotUseRawInterpolationForIdentifiersOrLiterals(string relativePath)
    {
        var file = Path.Combine(RepoRoot, relativePath);
        var fileName = Path.GetFileName(file);
        var violations = new List<string>();

        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!RawInterpolation.IsMatch(line))
                continue;
            if (Allowlist.Any(a => a.File == fileName && line.Contains(a.LineContains, StringComparison.Ordinal)))
                continue;
            violations.Add($"{fileName}:{i + 1}: {line.Trim()}");
        }

        Assert.True(violations.Count == 0,
            "Raw interpolation inside a SQL identifier ([{...}]) or string literal ('{...}') detected. " +
            "Use SqlQuoting.QuoteIdentifier / QuoteStringLiteral / QuoteNStringLiteral, or add a justified " +
            "allowlist entry if the line is provably not SQL:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Allowlist_HasNoStaleEntries()
    {
        var allLines = EnumerateScannedFiles()
            .SelectMany(f => File.ReadLines(f).Select(l => (FileName: Path.GetFileName(f), Line: l)))
            .ToList();

        var stale = Allowlist
            .Where(a => !allLines.Any(l => l.FileName == a.File && l.Line.Contains(a.LineContains, StringComparison.Ordinal)))
            .Select(a => $"{a.File}: \"{a.LineContains}\"")
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlist entries no longer match any source line — remove them:\n" + string.Join('\n', stale));
    }

    private static IEnumerable<string> EnumerateScannedFiles()
    {
        var sqlServerDir = Path.Combine(RepoRoot, "src", "MsSqlOperator", "Services", "SqlServer");
        foreach (var file in Directory.EnumerateFiles(sqlServerDir, "*.cs"))
        {
            // SqlQuoting itself legitimately builds quoted fragments.
            if (Path.GetFileName(file) == "SqlQuoting.cs")
                continue;
            yield return file;
        }
        yield return Path.Combine(RepoRoot, "src", "MsSqlOperator", "Services", "Kubernetes", "MssqlConfBuilder.cs");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException(
            "Cannot find repository root (.git directory) from " + AppContext.BaseDirectory);
    }
}
