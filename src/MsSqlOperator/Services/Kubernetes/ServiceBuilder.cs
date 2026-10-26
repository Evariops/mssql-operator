using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

public class ServiceBuilder : IServiceBuilder
{

    public V1Service BuildHeadless(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference? owner)
    {
        return new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = BuildServiceLabels(selector),
                OwnerReferences = owner is null ? null : [owner]
            },
            Spec = new V1ServiceSpec
            {
                ClusterIP = "None",
                Selector = selector,
                // Standard for distributed database StatefulSets — without it the
                // pod FQDNs do not resolve during recovery, slowing AG re-formation and
                // breaking seeding to a restarting replica.
                PublishNotReadyAddresses = true,
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
                Labels = BuildServiceLabels(selector),
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

    /// <summary>Propagate the operator labels onto generated Services so they are
    /// discoverable by label selectors (kubectl get svc -l ...).</summary>
    private static Dictionary<string, string> BuildServiceLabels(Dictionary<string, string> selector)
    {
        var labels = new Dictionary<string, string>(selector)
        {
            ["app.kubernetes.io/managed-by"] = "mssql-operator",
            // Common scoping label for the owned-resource watch selector.
            [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue,
        };
        return labels;
    }
}
