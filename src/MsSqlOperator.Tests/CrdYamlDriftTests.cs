using System.Reflection;
using System.Text.Json.Serialization;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace MsSqlOperator.Tests;

/// <summary>
/// Validates that the CRD YAML files in deploy/crds/ are consistent
/// with the C# POCO classes in MsSqlOperator.Entities.
/// Detects drifts: missing/extra properties, enum value mismatches, type mismatches.
/// Source of truth: C# code.
/// </summary>
public class CrdYamlDriftTests
{
    private readonly ITestOutputHelper _output;
    private static readonly string CrdDirectory = FindCrdDirectory();

    /// <summary>Maps CRD Kind → (C# spec type, C# status type)</summary>
    private static readonly Dictionary<string, (Type? SpecType, Type? StatusType)> KindToTypes = new()
    {
        ["SqlAgentJob"] = (typeof(SqlAgentJobSpec), typeof(SqlAgentJobStatus)),
        ["SqlAvailabilityGroupDatabase"] = (typeof(SqlAvailabilityGroupDatabaseSpec), typeof(SqlAvailabilityGroupDatabaseStatus)),
        ["SqlAvailabilityGroup"] = (typeof(SqlAvailabilityGroupSpec), typeof(SqlAvailabilityGroupStatus)),
        ["SqlConfiguration"] = (typeof(SqlConfigurationSpec), typeof(SqlConfigurationStatus)),
        ["SqlInstance"] = (typeof(SqlInstanceSpec), typeof(SqlInstanceStatus)),
        ["SqlStoredProcedure"] = (typeof(SqlStoredProcedureSpec), typeof(SqlStoredProcedureStatus)),
        ["SqlTdeCertificate"] = (typeof(SqlTdeCertificateSpec), typeof(SqlTdeCertificateStatus)),
        ["SqlUser"] = (typeof(SqlUserSpec), typeof(SqlUserStatus)),
    };

    /// <summary>Maps CRD Kind → root C# CRD class (for metadata constants from KubernetesEntity attribute)</summary>
    private static readonly Dictionary<string, Type> KindToRootType = new()
    {
        ["SqlAgentJob"] = typeof(SqlAgentJob),
        ["SqlAvailabilityGroupDatabase"] = typeof(SqlAvailabilityGroupDatabase),
        ["SqlAvailabilityGroup"] = typeof(SqlAvailabilityGroup),
        ["SqlConfiguration"] = typeof(SqlConfiguration),
        ["SqlInstance"] = typeof(SqlInstance),
        ["SqlStoredProcedure"] = typeof(SqlStoredProcedure),
        ["SqlTdeCertificate"] = typeof(SqlTdeCertificate),
        ["SqlUser"] = typeof(SqlUser),
    };

    /// <summary>Namespaces whose types we should NOT recurse into (k8s SDK types, etc.)</summary>
    private static readonly HashSet<string> ExternalNamespaces = ["k8s.Models"];

    public CrdYamlDriftTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AllCrdFiles_HaveMatchingPocoClasses()
    {
        var crdKinds = GetAllCrdKinds();
        var drifts = new List<string>();

        foreach (var kind in crdKinds)
        {
            if (!KindToTypes.ContainsKey(kind))
                drifts.Add($"CRD kind '{kind}' has no matching entry in KindToTypes mapping");
        }

        foreach (var kind in KindToTypes.Keys)
        {
            if (!crdKinds.Contains(kind))
                drifts.Add($"POCO mapping for kind '{kind}' has no matching CRD YAML file");
        }

        ReportAndAssert(drifts, "CRD ↔ POCO class mapping");
    }

    [Fact]
    public void AllCrdSpecProperties_ExistInPocoClasses()
    {
        var drifts = new List<string>();

        foreach (var (kind, specSchema, _) in EnumerateCrdSchemas())
        {
            if (specSchema == null) continue;
            if (!KindToTypes.TryGetValue(kind, out var types) || types.SpecType == null) continue;

            CompareSchemaToType(specSchema, types.SpecType, $"{kind}.spec", drifts);
        }

        ReportAndAssert(drifts, "CRD spec properties");
    }

    [Fact]
    public void AllCrdStatusProperties_ExistInPocoClasses()
    {
        var drifts = new List<string>();

        foreach (var (kind, _, statusSchema) in EnumerateCrdSchemas())
        {
            if (statusSchema == null) continue;
            if (!KindToTypes.TryGetValue(kind, out var types) || types.StatusType == null) continue;

            CompareSchemaToType(statusSchema, types.StatusType, $"{kind}.status", drifts);
        }

        ReportAndAssert(drifts, "CRD status properties");
    }

    [Fact]
    public void AllCrdEnumValues_MatchPocoEnums()
    {
        var drifts = new List<string>();

        foreach (var (kind, specSchema, statusSchema) in EnumerateCrdSchemas())
        {
            if (!KindToTypes.TryGetValue(kind, out var types)) continue;

            if (specSchema != null && types.SpecType != null)
                CompareEnums(specSchema, types.SpecType, $"{kind}.spec", drifts);

            if (statusSchema != null && types.StatusType != null)
                CompareEnums(statusSchema, types.StatusType, $"{kind}.status", drifts);
        }

        ReportAndAssert(drifts, "CRD enum values");
    }

    [Fact]
    public void AllPocoProperties_ExistInCrdYaml()
    {
        var drifts = new List<string>();

        foreach (var (kind, specSchema, statusSchema) in EnumerateCrdSchemas())
        {
            if (!KindToTypes.TryGetValue(kind, out var types)) continue;

            if (types.SpecType != null && specSchema != null)
                CompareTypeToSchema(types.SpecType, specSchema, $"{kind}.spec", drifts);

            if (types.StatusType != null && statusSchema != null)
                CompareTypeToSchema(types.StatusType, statusSchema, $"{kind}.status", drifts);
        }

        ReportAndAssert(drifts, "POCO → CRD reverse check");
    }

    [Fact]
    public void CrdMetadata_MatchesPocoAttributes()
    {
        var drifts = new List<string>();

        foreach (var doc in LoadAllCrdDocuments())
        {
            var kind = GetCrdKind(doc);
            if (kind == null || !KindToRootType.TryGetValue(kind, out var rootType)) continue;

            // Extract YAML metadata
            var yamlGroup = (NavigatePath(doc, "spec", "group") as YamlScalarNode)?.Value;
            var yamlPlural = (NavigatePath(doc, "spec", "names", "plural") as YamlScalarNode)?.Value;
            var yamlVersion = (NavigatePath(doc, "spec", "versions", "0", "name") as YamlScalarNode)?.Value;

            // Read C# metadata from KubernetesEntity attribute
            var attr = rootType.GetCustomAttribute<KubernetesEntityAttribute>();
            if (attr == null)
            {
                drifts.Add($"MISSING ATTRIBUTE: {kind} — {rootType.Name} has no [KubernetesEntity] attribute");
                continue;
            }

            if (yamlGroup != null && attr.Group != null && yamlGroup != attr.Group)
                drifts.Add($"GROUP MISMATCH: {kind} — CRD='{yamlGroup}', C#='{attr.Group}'");

            if (yamlVersion != null && attr.ApiVersion != null && yamlVersion != attr.ApiVersion)
                drifts.Add($"VERSION MISMATCH: {kind} — CRD='{yamlVersion}', C#='{attr.ApiVersion}'");

            if (yamlPlural != null && attr.PluralName != null && yamlPlural != attr.PluralName)
                drifts.Add($"PLURAL MISMATCH: {kind} — CRD='{yamlPlural}', C#='{attr.PluralName}'");
        }

        ReportAndAssert(drifts, "CRD metadata (group/version/plural)");
    }

    [Fact]
    public void DeepNestedProperties_AreConsistent()
    {
        var drifts = new List<string>();

        foreach (var (kind, specSchema, statusSchema) in EnumerateCrdSchemas())
        {
            if (!KindToTypes.TryGetValue(kind, out var types)) continue;

            if (specSchema != null && types.SpecType != null)
                CompareDeep(specSchema, types.SpecType, $"{kind}.spec", drifts, new HashSet<Type>());

            if (statusSchema != null && types.StatusType != null)
                CompareDeep(statusSchema, types.StatusType, $"{kind}.status", drifts, new HashSet<Type>());
        }

        ReportAndAssert(drifts, "deep nested properties");
    }

    // ── Schema comparison ───────────────────────────────────────────────

    private static void CompareSchemaToType(YamlMappingNode schema, Type type, string path, List<string> drifts)
    {
        var properties = GetSchemaProperties(schema);
        if (properties == null) return;

        var typeProps = GetCamelCasePropertyMap(type);

        foreach (var (yamlName, yamlNode) in properties)
        {
            if (!typeProps.TryGetValue(yamlName, out var propInfo))
            {
                drifts.Add($"MISSING IN C#: {path}.{yamlName} — exists in CRD YAML but not in {type.Name}");
                continue;
            }

            // Type check
            var yamlType = GetYamlType(yamlNode);
            var csharpType = GetCSharpJsonType(propInfo.PropertyType);

            if (yamlType != null && csharpType != null && yamlType != csharpType)
            {
                drifts.Add($"TYPE MISMATCH: {path}.{yamlName} — CRD={yamlType}, C#={csharpType} ({propInfo.PropertyType.Name})");
            }
        }
    }

    private static void CompareTypeToSchema(Type type, YamlMappingNode schema, string path, List<string> drifts)
    {
        var properties = GetSchemaProperties(schema);
        if (properties == null) return;

        var typeProps = GetCamelCasePropertyMap(type);
        var yamlNames = properties.Keys.ToHashSet();

        foreach (var (jsonName, propInfo) in typeProps)
        {
            if (propInfo.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
            if (propInfo.GetCustomAttribute<ObsoleteAttribute>() != null) continue;

            // Skip properties from k8s SDK base types
            if (propInfo.DeclaringType?.Namespace != null &&
                ExternalNamespaces.Contains(propInfo.DeclaringType.Namespace))
                continue;

            if (!yamlNames.Contains(jsonName))
            {
                drifts.Add($"MISSING IN CRD: {path}.{jsonName} — exists in {type.Name}.{propInfo.Name} but not in CRD YAML");
            }
        }
    }

    /// <summary>
    /// Recursively compares CRD YAML schema ↔ C# types at all nesting levels.
    /// </summary>
    private static void CompareDeep(YamlMappingNode schema, Type type, string path, List<string> drifts, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;

        var properties = GetSchemaProperties(schema);
        if (properties == null) return;

        var typeProps = GetCamelCasePropertyMap(type);

        foreach (var (yamlName, yamlNode) in properties)
        {
            if (yamlNode is not YamlMappingNode yamlMapping) continue;
            if (!typeProps.TryGetValue(yamlName, out var propInfo)) continue;

            var propType = UnwrapNullable(propInfo.PropertyType);

            // object with properties → recurse into nested type
            if (GetSchemaProperties(yamlMapping) != null)
            {
                var innerType = ResolveInnerType(propType);
                if (innerType != null && IsProjectType(innerType))
                {
                    CompareSchemaToType(yamlMapping, innerType, $"{path}.{yamlName}", drifts);
                    CompareTypeToSchema(innerType, yamlMapping, $"{path}.{yamlName}", drifts);
                    CompareDeep(yamlMapping, innerType, $"{path}.{yamlName}", drifts, visited);
                }
            }

            // array with object items → recurse into element type
            var itemSchema = GetArrayItemSchema(yamlMapping);
            if (itemSchema != null && GetSchemaProperties(itemSchema) != null)
            {
                var elementType = GetListElementType(propType);
                if (elementType != null && IsProjectType(elementType))
                {
                    CompareSchemaToType(itemSchema, elementType, $"{path}.{yamlName}[]", drifts);
                    CompareTypeToSchema(elementType, itemSchema, $"{path}.{yamlName}[]", drifts);
                    CompareDeep(itemSchema, elementType, $"{path}.{yamlName}[]", drifts, visited);
                }
            }

            // additionalProperties with object values → recurse into dictionary value type
            var additionalSchema = GetAdditionalPropertiesSchema(yamlMapping);
            if (additionalSchema != null && GetSchemaProperties(additionalSchema) != null)
            {
                var valueType = GetDictionaryValueType(propType);
                if (valueType != null && IsProjectType(valueType))
                {
                    CompareSchemaToType(additionalSchema, valueType, $"{path}.{yamlName}{{}}", drifts);
                    CompareTypeToSchema(valueType, additionalSchema, $"{path}.{yamlName}{{}}", drifts);
                    CompareDeep(additionalSchema, valueType, $"{path}.{yamlName}{{}}", drifts, visited);
                }
            }
        }

        visited.Remove(type); // allow same type at different paths
    }

    private void CompareEnums(YamlMappingNode schema, Type type, string path, List<string> drifts)
    {
        var properties = GetSchemaProperties(schema);
        if (properties == null) return;

        var typeProps = GetCamelCasePropertyMap(type);

        foreach (var (yamlName, yamlNode) in properties)
        {
            var enumValues = GetYamlEnumValues(yamlNode);
            if (enumValues is { Count: > 0 })
            {
                if (typeProps.TryGetValue(yamlName, out var propInfo))
                {
                    var propType = Nullable.GetUnderlyingType(propInfo.PropertyType) ?? propInfo.PropertyType;

                    if (propType.IsEnum)
                    {
                        var csharpValues = Enum.GetNames(propType).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var yamlVal in enumValues)
                        {
                            if (!csharpValues.Contains(yamlVal))
                                drifts.Add($"ENUM MISSING IN C#: {path}.{yamlName} value '{yamlVal}' — in CRD but not in {propType.Name}");
                        }

                        foreach (var csharpVal in Enum.GetNames(propType))
                        {
                            if (!enumValues.Any(v => v.Equals(csharpVal, StringComparison.OrdinalIgnoreCase)))
                                drifts.Add($"ENUM MISSING IN CRD: {path}.{yamlName} value '{csharpVal}' — in {propType.Name} but not in CRD YAML");
                        }
                    }
                    else if (propType == typeof(string))
                    {
                        _output.WriteLine($"INFO: {path}.{yamlName} has CRD enum {string.Join("|", enumValues)} but C# type is string");
                    }
                }
            }

            // Recurse into nested objects for enum checks
            if (yamlNode is YamlMappingNode mapping && typeProps.TryGetValue(yamlName, out var pi))
            {
                var pt = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;

                // For arrays, check items schema
                if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var itemSchema = GetArrayItemSchema(mapping);
                    if (itemSchema != null)
                    {
                        var elementType = pt.GetGenericArguments()[0];
                        if (IsProjectType(elementType))
                            CompareEnums(itemSchema, elementType, $"{path}.{yamlName}[]", drifts);
                    }
                }
                else if (GetSchemaProperties(mapping) != null && IsProjectType(pt))
                {
                    CompareEnums(mapping, pt, $"{path}.{yamlName}", drifts);
                }
            }
        }
    }

    // ── YAML parsing ────────────────────────────────────────────────────

    private static HashSet<string> GetAllCrdKinds()
    {
        var kinds = new HashSet<string>();
        foreach (var doc in LoadAllCrdDocuments())
        {
            var kind = GetCrdKind(doc);
            if (kind != null) kinds.Add(kind);
        }
        return kinds;
    }

    private static IEnumerable<(string Kind, YamlMappingNode? Spec, YamlMappingNode? Status)> EnumerateCrdSchemas()
    {
        foreach (var doc in LoadAllCrdDocuments())
        {
            var kind = GetCrdKind(doc);
            if (kind == null) continue;

            var openApiSchema = NavigatePath(doc, "spec", "versions", "0", "schema", "openAPIV3Schema", "properties");
            if (openApiSchema is not YamlMappingNode propsNode) continue;

            YamlMappingNode? specSchema = null;
            YamlMappingNode? statusSchema = null;

            if (propsNode.Children.TryGetValue(new YamlScalarNode("spec"), out var specNode) && specNode is YamlMappingNode specMapping)
                specSchema = GetSchemaProperties(specMapping) != null ? specMapping : null;

            if (propsNode.Children.TryGetValue(new YamlScalarNode("status"), out var statusNode) && statusNode is YamlMappingNode statusMapping)
                statusSchema = GetSchemaProperties(statusMapping) != null ? statusMapping : null;

            yield return (kind, specSchema, statusSchema);
        }
    }

    private static IEnumerable<YamlMappingNode> LoadAllCrdDocuments()
    {
        foreach (var file in Directory.GetFiles(CrdDirectory, "*.yaml"))
        {
            var yaml = new YamlStream();
            using var reader = new StreamReader(file);
            yaml.Load(reader);

            foreach (var doc in yaml.Documents)
            {
                if (doc.RootNode is YamlMappingNode root)
                    yield return root;
            }
        }
    }

    private static string? GetCrdKind(YamlMappingNode root)
    {
        if (NavigatePath(root, "spec", "names") is YamlMappingNode namesNode &&
            namesNode.Children.TryGetValue(new YamlScalarNode("kind"), out var kindNode) &&
            kindNode is YamlScalarNode scalar)
        {
            return scalar.Value;
        }
        return null;
    }

    private static Dictionary<string, YamlNode>? GetSchemaProperties(YamlMappingNode schema)
    {
        if (schema.Children.TryGetValue(new YamlScalarNode("properties"), out var propsNode) && propsNode is YamlMappingNode propsMapping)
        {
            return propsMapping.Children.ToDictionary(
                kv => ((YamlScalarNode)kv.Key).Value!,
                kv => kv.Value);
        }
        return null;
    }

    private static List<string>? GetYamlEnumValues(YamlNode node)
    {
        if (node is YamlMappingNode mapping &&
            mapping.Children.TryGetValue(new YamlScalarNode("enum"), out var enumNode) &&
            enumNode is YamlSequenceNode seq)
        {
            return seq.Children.OfType<YamlScalarNode>().Select(s => s.Value!).ToList();
        }
        return null;
    }

    private static YamlMappingNode? GetArrayItemSchema(YamlMappingNode node)
    {
        if (node.Children.TryGetValue(new YamlScalarNode("items"), out var itemsNode) && itemsNode is YamlMappingNode itemsMapping)
            return itemsMapping;
        return null;
    }

    private static YamlMappingNode? GetAdditionalPropertiesSchema(YamlMappingNode node)
    {
        if (node.Children.TryGetValue(new YamlScalarNode("additionalProperties"), out var apNode) && apNode is YamlMappingNode apMapping)
            return apMapping;
        return null;
    }

    private static string? GetYamlType(YamlNode node)
    {
        if (node is YamlMappingNode mapping &&
            mapping.Children.TryGetValue(new YamlScalarNode("type"), out var typeNode) &&
            typeNode is YamlScalarNode scalar)
        {
            return scalar.Value;
        }
        return null;
    }

    private static YamlNode? NavigatePath(YamlNode root, params string[] segments)
    {
        var current = root;
        foreach (var seg in segments)
        {
            if (current is YamlMappingNode mapping)
            {
                if (!mapping.Children.TryGetValue(new YamlScalarNode(seg), out var next))
                    return null;
                current = next;
            }
            else if (current is YamlSequenceNode seq && int.TryParse(seg, out var idx) && idx < seq.Children.Count)
            {
                current = seq.Children[idx];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    // ── Reflection helpers ──────────────────────────────────────────────

    /// <summary>
    /// Builds a map of camelCase property name → PropertyInfo for a type.
    /// Uses [JsonPropertyName] if present, otherwise converts PascalCase to camelCase.
    /// Also handles inherited properties from base classes (e.g. PreferredAntiAffinityRule : AntiAffinityRule).
    /// </summary>
    private static Dictionary<string, PropertyInfo> GetCamelCasePropertyMap(Type type)
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                           ?? char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            map[jsonName] = prop;
        }
        return map;
    }

    private static string? GetCSharpJsonType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string) || type == typeof(DateTimeOffset) || type == typeof(DateTime)) return "string";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type == typeof(bool)) return "boolean";
        if (type.IsEnum) return "string"; // enums serialize as strings
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return "array";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return "object";
        if (type.IsClass) return "object";
        return null;
    }

    // ── Type resolution helpers ─────────────────────────────────────────

    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsProjectType(Type type) =>
        type.Namespace != null &&
        type.Namespace.StartsWith("MsSqlOperator.", StringComparison.Ordinal) &&
        !ExternalNamespaces.Contains(type.Namespace);

    private static Type? ResolveInnerType(Type propType)
    {
        if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return propType.GetGenericArguments()[1];
        if (propType.IsClass && propType != typeof(string))
            return propType;
        return null;
    }

    private static Type? GetListElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    private static Type? GetDictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? type.GetGenericArguments()[1] : null;

    // ── Reporting ───────────────────────────────────────────────────────

    private void ReportAndAssert(List<string> drifts, string category)
    {
        foreach (var drift in drifts)
            _output.WriteLine($"  DRIFT: {drift}");

        if (drifts.Count > 0)
        {
            _output.WriteLine($"\n=== {drifts.Count} drift(s) detected in {category} ===\n");
            Assert.Fail($"{drifts.Count} CRD drift(s) detected in {category}:\n" + string.Join("\n", drifts));
        }
    }

    private static string FindCrdDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;

        if (dir == null)
            throw new DirectoryNotFoundException("Cannot find repository root (.git directory) from " + AppContext.BaseDirectory);

        var candidate = Path.Combine(dir, "deploy", "crds");
        if (Directory.Exists(candidate)) return candidate;

        throw new DirectoryNotFoundException($"Cannot find deploy/crds from {dir}");
    }
}
