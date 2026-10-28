using System.Reflection;
using System.Text.RegularExpressions;
using MsSqlOperator.Services.Actuation;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// A method marked <see cref="DestructiveAttribute"/> is called from Services/Actuation
/// only — through the gateway, with a decision of policy, traced, and its preconditions checked again.
/// </summary>
public class DestructiveCallsGoThroughTheGatewayTests
{
    private static readonly string SourceRoot = Path.Combine(FindRepoRoot(), "src", "MsSqlOperator");

    /// <summary>File, then why it may call a [Destructive] method directly.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        [Path.Combine("Services", "Breakout", "VolumeAttachmentFencingDriver.cs")] =
            "the fencing driver is the effect the gateway runs (DetachVolumesAsync, ForceDeletePodAsync)",
    };

    [Fact]
    public void DestructiveMethods_AreCalledFromTheActuationLayerOnly()
    {
        var methods = typeof(DestructiveAttribute).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<DestructiveAttribute>() is not null)
            .Select(m => m.Name)
            .Distinct()
            .ToList();
        Assert.NotEmpty(methods);

        var call = new Regex(@"\.(" + string.Join('|', methods) + @")\(");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SourceRoot, file);
            if (relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("bin", StringComparison.Ordinal)
                || relative.StartsWith(Path.Combine("Services", "Actuation"), StringComparison.Ordinal) || Allowed.ContainsKey(relative))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (call.Match(lines[i]) is { Success: true } m && !lines[i].Contains("Gateway.", StringComparison.Ordinal)
                    && !lines[i].Contains("gateway.", StringComparison.Ordinal) && !IsDeclaration(lines[i]))
                    hits.Add($"{relative}:{i + 1}: {m.Groups[1].Value}");
        }

        Assert.True(hits.Count == 0, "[Destructive] methods called outside Services/Actuation:\n" + string.Join("\n", hits));
    }

    private static bool IsDeclaration(string line) => line.TrimStart().StartsWith("public ", StringComparison.Ordinal)
                                                      || line.TrimStart().StartsWith("Task", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MsSqlOperator.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
