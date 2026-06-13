using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
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

        // The /status subresource only exists when the entity has a Status.
        var subresources = statusType is not null
            ? new V1CustomResourceSubresources { Status = new object() }
            : null;

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
                        Subresources = subresources,
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

    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly ConcurrentDictionary<Type, object?> DefaultInstances = new();

    private static V1JSONSchemaProps BuildObject(Type type, HashSet<Type> stack, int depth)
    {
        if (depth >= MaxDepth || !stack.Add(type))
            return new V1JSONSchemaProps { Type = "object", XKubernetesPreserveUnknownFields = true };

        try
        {
            var props = new Dictionary<string, V1JSONSchemaProps>();
            var required = new List<string>();
            var defaultInstance = DefaultInstances.GetOrAdd(type, static t =>
            {
                try { return Activator.CreateInstance(t); }
                catch { return null; }
            });

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0)
                    continue;
                // [JsonIgnore] properties never reach the wire — keep them out of
                // the schema (the drift test already skipped them; the codegen now agrees).
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                    continue;

                var name = JsonName(p);
                var schema = BuildSchema(p.PropertyType, stack, depth + 1);
                ApplyPropertyFacets(p, schema, defaultInstance);
                props[name] = schema;

                // A non-nullable reference type with no initializer (fresh-instance
                // value is null) can only be satisfied by the manifest — emit `required`.
                if (defaultInstance is not null
                    && !p.PropertyType.IsValueType
                    && Nullability.Create(p).WriteState == NullabilityState.NotNull
                    && SafeGet(p, defaultInstance) is null)
                    required.Add(name);
            }

            return new V1JSONSchemaProps
            {
                Type = "object",
                Properties = props,
                Required = required.Count > 0 ? required : null,
            };
        }
        finally
        {
            stack.Remove(type);
        }
    }

    /// <summary>Validation facets read from standard DataAnnotations, defaults from
    /// C# initializers, descriptions from XML docs, and CEL immutability
    /// rules from [Immutable].</summary>
    private static void ApplyPropertyFacets(PropertyInfo p, V1JSONSchemaProps schema, object? defaultInstance)
    {
        if (p.GetCustomAttribute<RegularExpressionAttribute>() is { } regex)
            schema.Pattern = regex.Pattern;

        if (p.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            schema.Minimum = Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture);
            schema.Maximum = Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (p.GetCustomAttribute<MaxLengthAttribute>() is { } maxLen)
        {
            if (schema.Type == "array") schema.MaxItems = maxLen.Length;
            else schema.MaxLength = maxLen.Length;
        }

        if (p.GetCustomAttribute<MinLengthAttribute>() is { } minLen)
        {
            if (schema.Type == "array") schema.MinItems = minLen.Length;
            else schema.MinLength = minLen.Length;
        }

        if (p.GetCustomAttribute<ImmutableAttribute>() is { } immutable)
            schema.XKubernetesValidations =
            [
                new V1ValidationRule
                {
                    Rule = "self == oldSelf",
                    Message = immutable.Reason ?? $"{JsonName(p)} is immutable",
                },
            ];

        schema.Description ??= XmlDocs.SummaryFor(p);

        // Surface C# initializer values as OpenAPI defaults (primitives only) so
        // `kubectl get -o yaml` shows effective values and default changes are explicit.
        if (defaultInstance is not null && schema.Type is "string" or "integer" or "number" or "boolean")
        {
            var value = SafeGet(p, defaultInstance);
            var clrDefault = p.PropertyType.IsValueType ? Activator.CreateInstance(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) : null;
            if (value is not null && !Equals(value, clrDefault) && value is not string { Length: 0 })
                schema.DefaultProperty = p.PropertyType.IsEnum || value is Enum ? value.ToString() : value;
        }
    }

    private static object? SafeGet(PropertyInfo p, object instance)
    {
        try { return p.GetValue(instance); }
        catch { return null; }
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
