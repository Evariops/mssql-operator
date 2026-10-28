using System.Text.RegularExpressions;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// A catch-all in a reconcile path swallowed the cancellation of the pass (reconcile timeout,
/// leadership loss, shutdown): the loop went on to the next element, and the pass outlived the
/// leader that started it. Every <c>catch (Exception)</c> of the controllers, finalizers and services
/// lets an <see cref="OperationCanceledException"/> of the caller's token through with a
/// <c>when (!ct.IsCancellationRequested)</c> filter — or rethrows.
/// </summary>
public partial class CancellationNotSwallowedTests
{
    private static readonly string SourceRoot = Path.Combine(FindRepoRoot(), "src", "MsSqlOperator");

    [GeneratedRegex(@"catch\s*\(\s*Exception(\s+\w+)?\s*\)\s*$")]
    private static partial Regex UnfilteredCatchAll();

    /// <summary>File, then why its unfiltered catch-all is right.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        [Path.Combine("Services", "SqlServer", "ProcDeployerLoginManager.cs")] =
            "ReleaseAsync runs after a cancellation on purpose, under its own 30 s timeout",
    };

    [Fact]
    public void CatchAlls_LetTheCallersCancellationThrough()
    {
        var hits = new List<string>();
        foreach (var dir in new[] { "Controllers", "Finalizers", "Services" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(SourceRoot, dir), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(SourceRoot, file);
                if (Allowed.ContainsKey(relative))
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    if (UnfilteredCatchAll().IsMatch(lines[i]) && !RethrowsAfter(lines, i))
                        hits.Add($"{relative}:{i + 1}");
            }

        Assert.True(hits.Count == 0,
            "catch (Exception) without a cancellation filter (add `when (!ct.IsCancellationRequested)`):\n" + string.Join("\n", hits));
    }

    /// <summary>A block that ends with a bare <c>throw;</c> lets the cancellation through.</summary>
    private static bool RethrowsAfter(string[] lines, int catchLine)
    {
        var depth = 0;
        for (var i = catchLine + 1; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            if (lines[i].Trim() == "throw;" && depth == 1)
                return true;
            if (depth <= 0 && lines[i].Contains('}'))
                return false;
        }
        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MsSqlOperator.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
