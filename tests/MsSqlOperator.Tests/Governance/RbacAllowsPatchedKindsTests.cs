using System.Text.RegularExpressions;
using k8s.Models;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// The operator writes PVCs by JSON patch only (a PUT is refused, see
/// <see cref="JsonPatch.NoPut"/>), but the ClusterRole still granted update on them: every volume
/// growth and every owner stamp was Forbidden on a test cluster, logged on every pass. A kind the code may
/// only patch carries the patch verb in the chart's ClusterRole.
/// </summary>
public partial class RbacAllowsPatchedKindsTests
{
    [Theory]
    [InlineData(typeof(V1PersistentVolumeClaim), "persistentvolumeclaims")]
    [InlineData(typeof(V1Node), "nodes")]
    public void KindPatchedInsteadOfPut_HasThePatchVerb(Type kind, string resource)
    {
        Assert.NotNull(JsonPatch.NoPut(kind));

        var rbac = File.ReadAllText(Path.Combine(FindRepoRoot(), "charts", "mssql-operator", "templates", "rbac.yaml"));
        var verbs = Rule().Matches(rbac)
            .Where(m => m.Groups["resources"].Value.Contains($"\"{resource}\"", StringComparison.Ordinal))
            .Select(m => m.Groups["verbs"].Value)
            .ToList();

        Assert.NotEmpty(verbs);
        Assert.Contains(verbs, v => v.Contains("\"patch\"", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"resources:\s*\[(?<resources>[^\]]*)\]\s*\n(?:\s*#.*\n)*\s*verbs:\s*\[(?<verbs>[^\]]*)\]")]
    private static partial Regex Rule();

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException(
            "Cannot find repository root (.git directory) from " + AppContext.BaseDirectory);
    }
}
