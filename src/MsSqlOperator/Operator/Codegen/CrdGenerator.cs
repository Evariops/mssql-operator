using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Operator.Codegen;

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

    // NullabilityInfoContext is not thread-safe: two CRDs generated at once (the tests run in
    // parallel) corrupted its cache and, now and then, the `required` lists of a schema.
    private static readonly ThreadLocal<NullabilityInfoContext> NullabilityContext = new(() => new NullabilityInfoContext());
    private static NullabilityInfoContext Nullability => NullabilityContext.Value!;
    private static readonly ConcurrentDictionary<Type, object?> DefaultInstances = new();

    private static V1JSONSchemaProps BuildObject(Type type, HashSet<Type> stack, int depth)
    {
        if (depth >= MaxDepth || !stack.Add(type))
            return new V1JSONSchemaProps { Type = "object", XKubernetesPreserveUnknownFields = true };

        try
        {
            var props = new Dictionary<string, V1JSONSchemaProps>();
            var required = new List<string>();
            var rules = new List<V1ValidationRule>();
            var defaultInstance = DefaultInstances.GetOrAdd(type, static t =>
            {
                try { return Activator.CreateInstance(t); }
                catch (Exception e) when (e is MissingMethodException or MemberAccessException or TargetInvocationException or NotSupportedException or ArgumentException) { return null; }
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
                if (ImmutabilityRule(p, name) is { } rule)
                    rules.Add(rule);

                // A non-nullable reference type with no initializer (fresh-instance
                // value is null) can only be satisfied by the manifest — emit `required`. So is a
                // property marked [Required], whatever its type or initializer.
                if (p.GetCustomAttribute<RequiredAttribute>() is not null
                    || (defaultInstance is not null
                        && !p.PropertyType.IsValueType
                        && Nullability.Create(p).WriteState == NullabilityState.NotNull
                        && SafeGet(p, defaultInstance) is null))
                    required.Add(name);
            }

            // Class-level CEL rules (transition rules included).
            foreach (var cel in type.GetCustomAttributes<CelRuleAttribute>())
                rules.Add(new V1ValidationRule { Rule = cel.Rule, Message = cel.Message });

            return new V1JSONSchemaProps
            {
                Type = "object",
                Properties = props,
                Required = required.Count > 0 ? required : null,
                XKubernetesValidations = rules.Count > 0 ? rules : null,
            };
        }
        finally
        {
            stack.Remove(type);
        }
    }

    /// <summary>Validation facets read from standard DataAnnotations, defaults from
    /// C# initializers and descriptions from XML docs. The CEL immutability rules
    /// of [Immutable] live on the enclosing object (<see cref="ImmutabilityRule"/>).</summary>
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

        schema.Description ??= XmlDocs.SummaryFor(p);

        // Surface C# initializer values as OpenAPI defaults (primitives only) so
        // `kubectl get -o yaml` shows effective values and default changes are explicit.
        // date-time is excluded: a "DateTime.UtcNow" initializer (e.g. LastTransitionTime) is a
        // runtime sentinel, never a meaningful — let alone deterministic — schema default. A
        // [Required] property has none: the manifest must state it.
        if (defaultInstance is not null
            && p.GetCustomAttribute<RequiredAttribute>() is null
            && schema.Type is "string" or "integer" or "number" or "boolean"
            && schema.Format != "date-time")
        {
            var value = SafeGet(p, defaultInstance);
            var clrDefault = p.PropertyType.IsValueType ? Activator.CreateInstance(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) : null;
            if (value is not null && !Equals(value, clrDefault) && value is not string { Length: 0 })
                schema.DefaultProperty = p.PropertyType.IsEnum || value is Enum ? value.ToString() : value;
        }
    }

    /// <summary>
    /// The CEL rule of an [Immutable] or [ImmutablePresence] property, carried by the enclosing
    /// object: a transition rule on the field itself is skipped whenever one version lacks
    /// it, so a field could be removed, then set again to another value.
    /// </summary>
    private static V1ValidationRule? ImmutabilityRule(PropertyInfo p, string name)
    {
        var field = CelFieldName(name);
        if (p.GetCustomAttribute<ImmutableAttribute>() is { } immutable)
            return new V1ValidationRule
            {
                Rule = $"has(self.{field}) == has(oldSelf.{field}) && (!has(self.{field}) || self.{field} == oldSelf.{field})",
                Message = immutable.Reason ?? $"{name} is immutable",
            };
        if (p.GetCustomAttribute<ImmutablePresenceAttribute>() is { } presence)
            return new V1ValidationRule
            {
                Rule = $"has(self.{field}) == has(oldSelf.{field})",
                Message = presence.Reason ?? $"{name} cannot be added or removed",
            };
        return null;
    }

    // CEL reserved words are escaped as __name__ by the API server's CEL environment.
    private static readonly HashSet<string> CelReserved =
    [
        "true", "false", "null", "in", "as", "break", "const", "continue", "else", "for", "function", "if",
        "import", "let", "loop", "package", "namespace", "return", "var", "void", "while",
    ];

    private static string CelFieldName(string name) => CelReserved.Contains(name) ? $"__{name}__" : name;

    private static object? SafeGet(PropertyInfo p, object instance)
    {
        try { return p.GetValue(instance); }
        catch (Exception e) when (e is TargetInvocationException or MemberAccessException or NotSupportedException or ArgumentException) { return null; }
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
        {
            var itemSchema = BuildSchema(element, stack, depth + 1);
            var array = new V1JSONSchemaProps { Type = "array", Items = itemSchema };
            // Condition arrays are map-lists keyed by type, so a server-side apply
            // merges them instead of replacing the whole array.
            if (element == typeof(Entities.Common.ConditionStatus))
            {
                array.XKubernetesListType = "map";
                array.XKubernetesListMapKeys = ["type"];
                // The apiserver rejects a CRD whose x-kubernetes-list-map-keys property has no
                // default and is not required. `type` is always set (ConditionOps.Set) and is
                // required by metav1.Condition, so mark it required on the item schema.
                itemSchema.Required ??= [];
                if (!itemSchema.Required.Contains("type"))
                    itemSchema.Required.Add("type");
            }
            return array;
        }

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
