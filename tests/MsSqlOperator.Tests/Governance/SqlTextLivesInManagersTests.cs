using System.Text.RegularExpressions;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// SQL text is sent from the managers (Services/SqlServer) and the connection layer
/// (Services/ConnectionFactory) only. A command built in a controller escaped the profiles, the
/// interpolation guard and the error taxonomy.
/// </summary>
public partial class SqlTextLivesInManagersTests
{
    private static readonly string SourceRoot = Path.Combine(FindRepoRoot(), "src", "MsSqlOperator");

    [GeneratedRegex(@"\.CreateCommand\(\)|new\s+(Microsoft\.Data\.SqlClient\.)?SqlCommand\b")]
    private static partial Regex Command();

    private static readonly string[] Allowed =
    [
        Path.Combine("Services", "SqlServer") + Path.DirectorySeparatorChar,
        Path.Combine("Services", "ConnectionFactory") + Path.DirectorySeparatorChar,
    ];

    [Fact]
    public void NoCommandIsBuiltOutsideTheManagers()
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SourceRoot, file);
            if (relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("bin", StringComparison.Ordinal)
                || Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal)))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (Command().IsMatch(lines[i]))
                    hits.Add($"{relative}:{i + 1}");
        }

        Assert.True(hits.Count == 0, "SQL commands built outside Services/SqlServer and Services/ConnectionFactory:\n" + string.Join("\n", hits));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MsSqlOperator.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
