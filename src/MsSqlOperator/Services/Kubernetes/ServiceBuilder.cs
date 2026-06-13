using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public class ServiceBuilder : IServiceBuilder
{
    public V1Service BuildClusterIp(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference owner)
    {
        return Build(name, ns, selector, new ServiceSpec { Type = "ClusterIP", Port = port }, owner);
    }

    public V1Service BuildHeadless(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference? owner)
    {
        return new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                OwnerReferences = owner is null ? null : [owner]
            },
            Spec = new V1ServiceSpec
            {
                ClusterIP = "None",
                Selector = selector,
                Ports = [new V1ServicePort { Port = port, TargetPort = port, Name = "sql" }]
            }
        };
    }

    public V1Service Build(string name, string ns, Dictionary<string, string> selector, ServiceSpec serviceSpec, V1OwnerReference? owner)
    {
        return new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Annotations = serviceSpec.Annotations,
                OwnerReferences = owner is null ? null : [owner]
            },
            Spec = new V1ServiceSpec
            {
                Type = serviceSpec.Type,
                Selector = selector,
                Ports = [new V1ServicePort { Port = serviceSpec.Port, TargetPort = 1433, Name = "sql" }]
            }
        };
    }
}
