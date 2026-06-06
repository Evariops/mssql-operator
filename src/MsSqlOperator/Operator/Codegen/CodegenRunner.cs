using System.Reflection;
using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// CLI entry for generating deploy artifacts (CRDs + RBAC), replacing <c>kubeops generate</c>.
/// Invoked via <c>dotnet run -- generate &lt;output-dir&gt;</c>. Discovers entity types by their
/// <see cref="KubernetesEntityAttribute"/> and our base class <see cref="CustomKubernetesEntity"/>.
/// </summary>
public static class CodegenRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var outDir = args.Length > 1 ? args[1] : Path.Combine("deploy", "crds");
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

        var role = RbacGenerator.Generate([assembly]);
        var rolePath = Path.Combine(outDir, "operator-role.yaml");
        await File.WriteAllTextAsync(rolePath, KubernetesYaml.Serialize(role));
        Console.WriteLine($"generated {rolePath}");

        Console.WriteLine($"Done: {entityTypes.Count} CRDs + RBAC written to {outDir}");
        return 0;
    }
}
