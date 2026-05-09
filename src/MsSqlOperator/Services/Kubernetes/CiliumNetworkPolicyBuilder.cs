using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace MsSqlOperator.Services.Kubernetes;

public interface ICiliumNetworkPolicyBuilder
{
    GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner);
    GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner);
}

public class CiliumNetworkPolicyBuilder : ICiliumNetworkPolicyBuilder
{
    public GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner)
    {
        var policy = new GenericCiliumNetworkPolicy
        {
            ApiVersion = "cilium.io/v2",
            Kind = "CiliumNetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = $"{name}-netpol",
                NamespaceProperty = ns,
                OwnerReferences = owner != null ? [owner] : null
            },
            Spec = new CiliumNetworkPolicySpec
            {
                EndpointSelector = new CiliumEndpointSelector { MatchLabels = labels },
                Ingress =
                [
                    new CiliumIngressRule
                    {
                        FromEndpoints = [new CiliumEndpointSelector()], // Same namespace only
                        ToPorts =
                        [
                            new CiliumPortRule
                            {
                                Ports = [new CiliumPort { Port = sqlPort.ToString(), Protocol = "TCP" }]
                            }
                        ]
                    }
                ],
                Egress =
                [
                    new CiliumEgressRule
                    {
                        ToEndpoints = [new CiliumEndpointSelector()],
                        ToPorts =
                        [
                            new CiliumPortRule
                            {
                                Ports =
                                [
                                    new CiliumPort { Port = "53", Protocol = "UDP" }, // DNS
                                    new CiliumPort { Port = "53", Protocol = "TCP" }
                                ]
                            }
                        ]
                    }
                ]
            }
        };
        return policy;
    }

    public GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner)
    {
        var policy = BuildForInstance(name, ns, labels, sqlPort, owner);

        // Add AG mirroring port for inter-replica communication
        policy.Spec.Ingress!.Add(new CiliumIngressRule
        {
            FromEndpoints = [new CiliumEndpointSelector { MatchLabels = labels }], // Same AG pods
            ToPorts =
            [
                new CiliumPortRule
                {
                    Ports = [new CiliumPort { Port = mirroringPort.ToString(), Protocol = "TCP" }]
                }
            ]
        });

        // Allow egress to other AG replicas on mirroring port
        policy.Spec.Egress!.Add(new CiliumEgressRule
        {
            ToEndpoints = [new CiliumEndpointSelector { MatchLabels = labels }],
            ToPorts =
            [
                new CiliumPortRule
                {
                    Ports =
                    [
                        new CiliumPort { Port = mirroringPort.ToString(), Protocol = "TCP" },
                        new CiliumPort { Port = sqlPort.ToString(), Protocol = "TCP" }
                    ]
                }
            ]
        });

        return policy;
    }
}

// Cilium CRD types (not part of k8s client, defined as generic objects)
public class GenericCiliumNetworkPolicy : IKubernetesObject<V1ObjectMeta>
{
    public string ApiVersion { get; set; } = "cilium.io/v2";
    public string Kind { get; set; } = "CiliumNetworkPolicy";
    public V1ObjectMeta Metadata { get; set; } = new();
    public CiliumNetworkPolicySpec Spec { get; set; } = new();
}

public class CiliumNetworkPolicySpec
{
    public CiliumEndpointSelector? EndpointSelector { get; set; }
    public List<CiliumIngressRule>? Ingress { get; set; }
    public List<CiliumEgressRule>? Egress { get; set; }
}

public class CiliumEndpointSelector
{
    public Dictionary<string, string>? MatchLabels { get; set; }
}

public class CiliumIngressRule
{
    public List<CiliumEndpointSelector>? FromEndpoints { get; set; }
    public List<CiliumPortRule>? ToPorts { get; set; }
}

public class CiliumEgressRule
{
    public List<CiliumEndpointSelector>? ToEndpoints { get; set; }
    public List<CiliumPortRule>? ToPorts { get; set; }
}

public class CiliumPortRule
{
    public List<CiliumPort>? Ports { get; set; }
}

public class CiliumPort
{
    public string Port { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
}
