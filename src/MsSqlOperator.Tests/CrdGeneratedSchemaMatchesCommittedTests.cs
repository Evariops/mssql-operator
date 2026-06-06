using System.Reflection;
using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace MsSqlOperator.Tests;

/// <summary>
/// Walks every CRD POCO, derives the expected OpenAPI schema with <see cref="CrdGenerator"/>, and
/// asserts the committed CRD YAML matches it structurally and by type: same property set at every
/// level, same scalar type + format, same array item / map value types, same enum values.
/// This is the authoritative POCO ⇄ YAML type-exactness guard (formatting, descriptions, nullable
/// and required markers are intentionally ignored).
/// </summary>
public class CrdGeneratedSchemaMatchesCommittedTests(ITestOutputHelper output)
{
    private static string CrdDirectory => Path.Combine(FindRepoRoot(), "charts", "mssql-operator", "crds");

    public static IEnumerable<object[]> EntityTypes =>
        typeof(SqlInstance).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false }
                        && typeof(CustomKubernetesEntity).IsAssignableFrom(t)
                        && t.GetCustomAttribute<KubernetesEntityAttribute>() is not null)
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public void CommittedCrd_MatchesPocoDerivedSchema(Type entityType)
    {
        var gvk = GroupVersionKind.Of(entityType);
        var file = Path.Combine(CrdDirectory, $"{gvk.PluralName}_{gvk.Group.Replace('.', '_')}.yaml");
        File.Exists(file).Should().BeTrue($"committed CRD '{file}' should exist");

        var committedSchema = LoadOpenApiSchema(file);
        var generated = CrdGenerator.Generate(entityType).Spec.Versions.Single().Schema.OpenAPIV3Schema;

        var drifts = new List<string>();
        foreach (var section in new[] { "spec", "status" })
        {
            var gen = generated.Properties.TryGetValue(section, out var g) ? g : null;
            var com = Child(Child(committedSchema, "properties"), section);
            if (gen is null && com is null)
                continue;
            if (gen is null || com is null)
            {
                drifts.Add($"{section}: present in {(gen is null ? "YAML only" : "POCO only")}");
                continue;
            }
            Compare(gen, (YamlMappingNode)com, section, drifts);
        }

        if (drifts.Count > 0)
            output.WriteLine($"{entityType.Name}:\n  " + string.Join("\n  ", drifts));

        drifts.Should().BeEmpty($"committed CRD for {entityType.Name} must match the POCO-derived schema");
    }

    private static void Compare(V1JSONSchemaProps gen, YamlMappingNode com, string path, List<string> drifts)
    {
        var comType = Scalar(com, "type");
        if (gen.Type != comType)
        {
            drifts.Add($"{path}: type POCO='{gen.Type}' YAML='{comType}'");
            return;
        }

        if (gen.Format is not null)
        {
            var comFormat = Scalar(com, "format");
            if (comFormat != gen.Format)
                drifts.Add($"{path}: format POCO='{gen.Format}' YAML='{comFormat}'");
        }

        if (gen.EnumProperty is { Count: > 0 })
        {
            var genValues = gen.EnumProperty.Select(v => v?.ToString()).OrderBy(v => v).ToList();
            var comValues = (Child(com, "enum") as YamlSequenceNode)?.Children
                .OfType<YamlScalarNode>().Select(n => n.Value).OrderBy(v => v).ToList() ?? [];
            if (!genValues.SequenceEqual(comValues))
                drifts.Add($"{path}: enum POCO=[{string.Join(",", genValues)}] YAML=[{string.Join(",", comValues)}]");
        }

        switch (gen.Type)
        {
            case "object" when gen.Properties is { Count: > 0 }:
            {
                var comProps = Child(com, "properties") as YamlMappingNode;
                if (comProps is null)
                {
                    if (IsPreserveUnknown(com))
                        return; // YAML opted out of a typed schema for this subtree
                    drifts.Add($"{path}: POCO has properties but YAML has none");
                    return;
                }

                var genKeys = gen.Properties.Keys.ToHashSet();
                var comKeys = comProps.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToHashSet();

                foreach (var missing in genKeys.Except(comKeys))
                    drifts.Add($"{path}.{missing}: in POCO, missing from YAML");
                foreach (var extra in comKeys.Except(genKeys))
                    drifts.Add($"{path}.{extra}: in YAML, missing from POCO");

                foreach (var key in genKeys.Intersect(comKeys))
                    Compare(gen.Properties[key], (YamlMappingNode)comProps.Children[new YamlScalarNode(key)], $"{path}.{key}", drifts);
                break;
            }
            case "object" when gen.AdditionalProperties is V1JSONSchemaProps mapValue:
            {
                if (Child(com, "additionalProperties") is YamlMappingNode comAp)
                    Compare(mapValue, comAp, $"{path}[*]", drifts);
                break;
            }
            case "array" when gen.Items is V1JSONSchemaProps items:
            {
                if (Child(com, "items") is YamlMappingNode comItems)
                    Compare(items, comItems, $"{path}[]", drifts);
                else
                    drifts.Add($"{path}: POCO array has typed items, YAML does not");
                break;
            }
        }
    }

    // ── YAML helpers ────────────────────────────────────────────────────

    private static YamlMappingNode LoadOpenApiSchema(string file)
    {
        using var reader = new StreamReader(file);
        var stream = new YamlStream();
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var version = ((YamlSequenceNode)Child(Child(root, "spec")!, "versions")!).Children.OfType<YamlMappingNode>().First();
        return (YamlMappingNode)Child(Child(version, "schema")!, "openAPIV3Schema")!;
    }

    private static YamlNode? Child(YamlNode? node, string key)
        => node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    private static bool IsPreserveUnknown(YamlMappingNode node)
        => Scalar(node, "x-kubernetes-preserve-unknown-fields") == "true";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repository root (.git directory).");
    }
}
