using System.Reflection;
using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// CLI entry for generating the chart CRDs, replacing <c>kubeops generate</c>.
/// Invoked via <c>dotnet run -- generate &lt;output-dir&gt;</c>. Discovers entity types by their
/// <see cref="KubernetesEntityAttribute"/> and our base class <see cref="CustomKubernetesEntity"/>.
/// RBAC is intentionally NOT generated here: the operator's ClusterRole is hand-maintained (and
/// kept stricter than the auto-discovered superset) in charts/mssql-operator/templates/rbac.yaml.
/// </summary>
public static class CodegenRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var outDir = args.Length > 1 ? args[1] : Path.Combine("charts", "mssql-operator", "templates", "crds");
        Directory.CreateDirectory(outDir);

        var assembly = typeof(CodegenRunner).Assembly;
        var entityTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false }
                        && typeof(CustomKubernetesEntity).IsAssignableFrom(t)
                        && t.GetCustomAttribute<KubernetesEntityAttribute>() is not null)
            .OrderBy(t => t.Name)
            .ToList();

        foreach (var entity in entityTypes)
        {
            var crd = CrdGenerator.Generate(entity);
            var attr = entity.GetCustomAttribute<KubernetesEntityAttribute>()!;
            var fileName = $"{(string.IsNullOrEmpty(attr.PluralName) ? attr.Kind.ToLowerInvariant() + "s" : attr.PluralName)}_{(attr.Group ?? "").Replace('.', '_')}.yaml";
            var path = Path.Combine(outDir, fileName);
            await File.WriteAllTextAsync(path, KubernetesYaml.Serialize(crd));
            Console.WriteLine($"generated {path}");
        }

        Console.WriteLine($"Done: {entityTypes.Count} CRDs written to {outDir}");
        return 0;
    }
}
