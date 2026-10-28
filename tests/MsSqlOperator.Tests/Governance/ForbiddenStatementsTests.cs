using System.Text.RegularExpressions;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// Statements the operator must never send, whatever the code path (plan of 2026-10-27, Phase 0).
/// Scans every C# source of src/MsSqlOperator.
/// </summary>
public class ForbiddenStatementsTests
{
    private static readonly string SourceRoot = Path.Combine(FindRepoRoot(), "src", "MsSqlOperator");

    /// <summary>Pattern, then why it is forbidden.</summary>
    private static readonly (Regex Pattern, string Why)[] Forbidden =
    [
        (new Regex(@"FORCE\s+REGENERATE", RegexOptions.IgnoreCase),
            "Regenerating the Service Master Key loses what it protects for good; a human decides."),
    ];

    [Fact]
    public void NoSourceFile_ContainsAForbiddenStatement()
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var (pattern, why) in Forbidden)
                    if (pattern.IsMatch(lines[i]))
                        hits.Add($"{Path.GetRelativePath(SourceRoot, file)}:{i + 1}: {why}");
        }

        Assert.True(hits.Count == 0, "Forbidden statements found:\n" + string.Join("\n", hits));
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
