using System.Diagnostics;
using FluentAssertions;

namespace MsSqlOperator.Tests;

public class CrdDriftTests
{
    private static readonly string[] CrdFiles =
    [
        "sqlagentjobs_mssql_operator.yaml",
        "sqlavailabilitygroupdatabases_mssql_operator.yaml",
        "sqlavailabilitygroups_mssql_operator.yaml",
        "sqlconfigurations_mssql_operator.yaml",
        "sqlinstances_mssql_operator.yaml",
        "sqlstoredprocedures_mssql_operator.yaml",
        "sqltdecertificates_mssql_operator.yaml",
        "sqlusers_mssql_operator.yaml",
    ];

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repository root (.git directory).");
    }

    [Fact]
    public async Task Committed_CRDs_should_match_generated_CRDs()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "MsSqlOperator", "MsSqlOperator.csproj");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"crd-drift-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tmpDir);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"kubeops generate operator mssql-operator \"{projectPath}\" --out \"{tmpDir}\" --format Yaml",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, $"kubeops generate failed: {await process.StandardError.ReadToEndAsync()}");

            var driftErrors = new List<string>();

            foreach (var crdFile in CrdFiles)
            {
                var generatedPath = Path.Combine(tmpDir, crdFile);
                File.Exists(generatedPath).Should().BeTrue($"generated CRD file '{crdFile}' should exist");

                var generated = NormalizeYaml(await File.ReadAllTextAsync(generatedPath));

                // Check deploy/crds
                var deployPath = Path.Combine(repoRoot, "deploy", "crds", crdFile);
                if (File.Exists(deployPath))
                {
                    var committed = NormalizeYaml(await File.ReadAllTextAsync(deployPath));
                    if (generated != committed)
                        driftErrors.Add($"deploy/crds/{crdFile} is out of date");
                }
                else
                {
                    driftErrors.Add($"deploy/crds/{crdFile} is missing");
                }

                // Check charts/mssql-operator/crds
                var chartPath = Path.Combine(repoRoot, "charts", "mssql-operator", "crds", crdFile);
                if (File.Exists(chartPath))
                {
                    var committed = NormalizeYaml(await File.ReadAllTextAsync(chartPath));
                    if (generated != committed)
                        driftErrors.Add($"charts/mssql-operator/crds/{crdFile} is out of date");
                }
                else
                {
                    driftErrors.Add($"charts/mssql-operator/crds/{crdFile} is missing");
                }
            }

            driftErrors.Should().BeEmpty(
                "CRDs are out of sync with entity definitions. " +
                "Run 'dotnet kubeops generate operator mssql-operator src/MsSqlOperator/MsSqlOperator.csproj --out deploy/crds --format Yaml' " +
                "and copy CRD files to charts/mssql-operator/crds/, then commit.");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static string NormalizeYaml(string content) =>
        content.ReplaceLineEndings("\n").TrimEnd();
}
