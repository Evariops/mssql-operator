namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// Only the release workflow signs and attests, and only for a published release: the images built
/// from main and from pull requests are development builds, never signed, so that a policy pinned on
/// the identity of the release workflow refuses them (docs/guide/operating.md#image-verification).
/// </summary>
public class ReleaseSigningTests
{
    private static readonly string[] SigningMarkers =
        ["cosign sign", "cosign attest", "id-token: write", "attestations: write", "attest-build-provenance"];

    public static IEnumerable<object[]> Workflows =>
        Directory.GetFiles(Path.Combine(RepoRoot(), ".github", "workflows"), "*.yml")
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Workflows))]
    public void OnlyTheReleaseWorkflow_SignsOrAttests(string workflow)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", workflow)).ReplaceLineEndings("\n");
        var signs = SigningMarkers.Where(text.Contains).ToList();

        if (workflow == "release.yml")
        {
            Assert.NotEmpty(signs);
            var on = text[text.IndexOf("\non:\n", StringComparison.Ordinal)..text.IndexOf("\npermissions:", StringComparison.Ordinal)];
            Assert.Equal("\non:\n  release:\n    types: [published]\n", on);
        }
        else
        {
            Assert.True(signs.Count == 0, $"{workflow} signs or attests ({string.Join(", ", signs)}): only release.yml may.");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MsSqlOperator.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
