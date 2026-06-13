using System.Reflection;
using k8s;
using k8s.Models;

namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Base class for custom resources, replacing KubeOps' <c>CustomKubernetesEntity</c>.
/// Implements the official client's <see cref="IKubernetesObject{V1ObjectMeta}"/> and
/// auto-populates <see cref="ApiVersion"/>/<see cref="Kind"/> from the
/// <see cref="KubernetesEntityAttribute"/> so freshly constructed instances are valid
/// API bodies for create/update.
/// </summary>
public abstract class CustomKubernetesEntity : IKubernetesObject<V1ObjectMeta>
{
    // The attribute lookup runs for every deserialized watch event — cache it per type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string ApiVersion, string Kind)> Gvk = new();

    protected CustomKubernetesEntity()
    {
        var (apiVersion, kind) = Gvk.GetOrAdd(GetType(), static t =>
        {
            var attr = t.GetCustomAttribute<KubernetesEntityAttribute>();
            if (attr is null)
                return (string.Empty, string.Empty);
            return (string.IsNullOrEmpty(attr.Group) ? attr.ApiVersion : $"{attr.Group}/{attr.ApiVersion}", attr.Kind);
        });
        ApiVersion = apiVersion;
        Kind = kind;
    }

    public string ApiVersion { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public V1ObjectMeta Metadata { get; set; } = new();
}

public abstract class CustomKubernetesEntity<TSpec> : CustomKubernetesEntity, ISpec<TSpec>
    where TSpec : new()
{
    public TSpec Spec { get; set; } = new();
}

public abstract class CustomKubernetesEntity<TSpec, TStatus> : CustomKubernetesEntity<TSpec>, IStatus<TStatus>
    where TSpec : new()
    where TStatus : new()
{
    public TStatus Status { get; set; } = new();
}
