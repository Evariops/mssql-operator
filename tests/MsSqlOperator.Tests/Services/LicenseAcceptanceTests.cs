using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Codegen;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The SQL Server license agreement is accepted by the manifest, and the edition is named
/// by it: the schema requires both, and the pods only get ACCEPT_EULA from an accepting spec.</summary>
public class LicenseAcceptanceTests
{
    private readonly StatefulSetBuilder _builder = new(new AntiAffinityBuilder(), new TopologySpreadBuilder());

    private static SqlInstance Instance(bool acceptEula) => new()
    {
        Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Uid = "uid-1" },
        Spec = new SqlInstanceSpec { AcceptEula = acceptEula, Edition = "Developer" },
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Pods_GetAcceptEula_OnlyFromAnAcceptingSpec(bool accepted, bool expected)
    {
        var env = _builder.Build(Instance(accepted), null).Spec.Template.Spec.Containers[0].Env;

        Assert.Equal(expected, env.Any(e => e is { Name: "ACCEPT_EULA", Value: "Y" }));
        Assert.Contains(env, e => e is { Name: "MSSQL_PID", Value: "Developer" });
    }

    [Theory]
    [InlineData(typeof(SqlInstance), "^(Developer|Express|Standard|Enterprise|EnterpriseCore)$")]
    [InlineData(typeof(SqlAvailabilityGroup), "^(Developer|Standard|Enterprise)$")]
    public void Schema_RequiresAcceptEulaAndEdition_WithoutDefault(Type kind, string editionPattern)
    {
        var spec = CrdGenerator.Generate(kind).Spec.Versions.Single().Schema.OpenAPIV3Schema.Properties["spec"];

        Assert.Contains("acceptEula", spec.Required);
        Assert.Contains("edition", spec.Required);
        Assert.Null(spec.Properties["edition"].DefaultProperty);
        Assert.Equal(editionPattern, spec.Properties["edition"].Pattern);
        Assert.Contains(spec.XKubernetesValidations, r => r.Rule == "self.acceptEula == true");
    }

    [Theory]
    [InlineData("sqlinstances_mssql-operator_evariops_com.yaml")]
    [InlineData("sqlavailabilitygroups_mssql-operator_evariops_com.yaml")]
    public void CommittedCrd_CarriesTheSameRequirements(string file)
    {
        var yaml = File.ReadAllText(Path.Combine(RepoRoot(), "charts", "mssql-operator", "templates", "crds", file));

        Assert.Contains("            required:\n            - acceptEula\n            - edition\n", yaml.ReplaceLineEndings("\n"));
        Assert.Contains("rule: self.acceptEula == true", yaml);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MsSqlOperator.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
