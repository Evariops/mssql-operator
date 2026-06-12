using System.Collections.Concurrent;
using System.Reflection;
using k8s.Models;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Group/Version/Kind/Plural resolved from a type's <see cref="KubernetesEntityAttribute"/>.
/// Works uniformly for native k8s.Models types and our custom resources.
/// </summary>
public sealed record GroupVersionKind(string Group, string Version, string Kind, string PluralName)
{
    private static readonly ConcurrentDictionary<Type, GroupVersionKind> Cache = new();

    public string ApiVersion => string.IsNullOrEmpty(Group) ? Version : $"{Group}/{Version}";

    public bool IsCoreGroup => string.IsNullOrEmpty(Group);

    public static GroupVersionKind Of(Type type) => Cache.GetOrAdd(type, static t =>
    {
        var attr = t.GetCustomAttribute<KubernetesEntityAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{t.FullName}' is missing [KubernetesEntity]; cannot resolve its API group/version/kind.");

        // Never guess the plural — ToLower()+"s" silently produces wrong URLs
        // for kinds with irregular plurals. Every entity must declare it.
        var plural = !string.IsNullOrEmpty(attr.PluralName)
            ? attr.PluralName
            : throw new InvalidOperationException(
                $"Type '{t.FullName}' must declare PluralName in [KubernetesEntity].");

        return new GroupVersionKind(attr.Group ?? string.Empty, attr.ApiVersion, attr.Kind, plural);
    });

    public static GroupVersionKind Of<T>() => Of(typeof(T));
}
