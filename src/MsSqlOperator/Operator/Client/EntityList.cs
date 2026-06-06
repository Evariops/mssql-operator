using k8s;
using k8s.Models;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Minimal list wrapper implementing <see cref="IKubernetesObject{V1ListMeta}"/> so it can be used
/// as the deserialization target for <see cref="GenericClient"/> list calls (which constrain their
/// type parameter to <see cref="IKubernetesObject"/>). Custom resources have no generated list type.
/// </summary>
public sealed class EntityList<T> : IKubernetesObject<V1ListMeta>, IItems<T>
    where T : IKubernetesObject<V1ObjectMeta>
{
    public string ApiVersion { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public V1ListMeta Metadata { get; set; } = new();

    public IList<T> Items { get; set; } = [];
}
