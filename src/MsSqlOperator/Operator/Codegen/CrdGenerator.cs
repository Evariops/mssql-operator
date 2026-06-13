using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Generates CustomResourceDefinitions from the entity POCOs by reflection, replacing the crashing
/// <c>kubeops generate</c>. Unlike KubeOps' transpiler, the schema walk is depth- and cycle-guarded:
/// recursive or excessively deep subtrees collapse to <c>x-kubernetes-preserve-unknown-fields</c>
/// instead of overflowing the stack.
/// </summary>
public static class CrdGenerator
{
    private const int MaxDepth = 12;

    public static V1CustomResourceDefinition Generate(Type entityType)
    {
        var entityAttr = entityType.GetCustomAttribute<KubernetesEntityAttribute>()
            ?? throw new InvalidOperationException($"{entityType.Name} is missing [KubernetesEntity].");
        var gvk = GroupVersionKind.Of(entityType);
        var shortNames = entityType.GetCustomAttribute<KubernetesEntityShortNamesAttribute>()?.ShortNames;

        var printerColumns = entityType.GetCustomAttributes<PrinterColumnAttribute>()
            .Select(c => new V1CustomResourceColumnDefinition
            {
                Name = c.Name,
                JsonPath = c.JsonPath,
                Type = c.Type,
                Priority = c.Priority == 0 ? null : c.Priority,
            })
            .ToList();

        var specType = entityType.GetProperty("Spec")?.PropertyType;
        var statusType = entityType.GetProperty("Status")?.PropertyType;

        var properties = new Dictionary<string, V1JSONSchemaProps>
        {
            ["apiVersion"] = new() { Type = "string" },
            ["kind"] = new() { Type = "string" },
            ["metadata"] = new() { Type = "object" },
        };
        if (specType is not null)
            properties["spec"] = BuildObject(specType, [], 0);
        if (statusType is not null)
            properties["status"] = BuildObject(statusType, [], 0);

        return new V1CustomResourceDefinition
        {
            ApiVersion = "apiextensions.k8s.io/v1",
            Kind = "CustomResourceDefinition",
            Metadata = new V1ObjectMeta
            {
                Name = $"{gvk.PluralName}.{gvk.Group}",
                // CRDs live in templates/ so Helm updates them on every upgrade;
                // keep prevents deletion on uninstall — CRs survive.
                Annotations = new Dictionary<string, string> { ["helm.sh/resource-policy"] = "keep" },
            },
            Spec = new V1CustomResourceDefinitionSpec
            {
                Group = gvk.Group,
                Scope = "Namespaced",
                Names = new V1CustomResourceDefinitionNames
                {
                    Kind = entityAttr.Kind,
                    ListKind = $"{entityAttr.Kind}List",
                    Plural = gvk.PluralName,
                    Singular = entityAttr.Kind.ToLowerInvariant(),
                    ShortNames = shortNames,
                },
                Versions =
                [
                    new V1CustomResourceDefinitionVersion
                    {
                        Name = gvk.Version,
                        Served = true,
                        Storage = true,
                        Subresources = new V1CustomResourceSubresources { Status = new object() },
                        AdditionalPrinterColumns = printerColumns.Count > 0 ? printerColumns : null,
                        Schema = new V1CustomResourceValidation
                        {
                            OpenAPIV3Schema = new V1JSONSchemaProps { Type = "object", Properties = properties },
                        },
                    },
                ],
            },
        };
    }

    private static V1JSONSchemaProps BuildObject(Type type, HashSet<Type> stack, int depth)
    {
        if (depth >= MaxDepth || !stack.Add(type))
            return new V1JSONSchemaProps { Type = "object", XKubernetesPreserveUnknownFields = true };

        try
        {
            var props = new Dictionary<string, V1JSONSchemaProps>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0)
                    continue;
                props[JsonName(p)] = BuildSchema(p.PropertyType, stack, depth + 1);
            }

            return new V1JSONSchemaProps { Type = "object", Properties = props };
        }
        finally
        {
            stack.Remove(type);
        }
    }

    private static V1JSONSchemaProps BuildSchema(Type type, HashSet<Type> stack, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return new() { Type = "string" };
        if (type == typeof(bool)) return new() { Type = "boolean" };
        if (type == typeof(int) || type == typeof(short) || type == typeof(byte))
            return new() { Type = "integer", Format = "int32" };
        if (type == typeof(long)) return new() { Type = "integer", Format = "int64" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new() { Type = "number" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new() { Type = "string", Format = "date-time" };
        if (type.IsEnum)
            return new() { Type = "string", EnumProperty = Enum.GetNames(type).Cast<object>().ToList() };

        // Dictionary<string, V> → object with additionalProperties
        var dict = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dict is not null)
            return new()
            {
                Type = "object",
                AdditionalProperties = BuildSchema(dict.GetGenericArguments()[1], stack, depth + 1),
            };

        // Arrays / IEnumerable<T> → array
        var element = ElementType(type);
        if (element is not null)
            return new() { Type = "array", Items = BuildSchema(element, stack, depth + 1) };

        // Complex object
        return BuildObject(type, stack, depth);
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    // Honour [JsonPropertyName] (e.g. k8s.Models' V1Toleration.OperatorProperty → "operator")
    // before falling back to camelCase, matching how the object is actually serialized.
    private static string JsonName(PropertyInfo p)
    {
        var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
        return attr is not null ? attr.Name : CamelCase(p.Name);
    }

    private static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
