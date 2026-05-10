using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace MsSqlOperator.Services.Kubernetes;

public interface ICiliumNetworkPolicyBuilder
{
    GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null);
    GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null);
}

public class CiliumNetworkPolicyBuilder : ICiliumNetworkPolicyBuilder
{
    public GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null)
    {
        var ingress = new List<CiliumIngressRule>
        {
            // Same namespace ingress
            new()
            {
                FromEndpoints = [new CiliumEndpointSelector()],
                ToPorts =
                [
                    new CiliumPortRule
                    {
                        Ports = [new CiliumPort { Port = sqlPort.ToString(), Protocol = "TCP" }]
                    }
                ]
            }
        };

        // Additional namespace ingress rules
        AddNamespaceIngressRules(ingress, allowedNamespaces, operatorNamespace, sqlPort);

        var egress = new List<CiliumEgressRule>
        {
            // DNS egress
            new()
            {
                ToEndpoints = [new CiliumEndpointSelector()],
                ToPorts =
                [
                    new CiliumPortRule
                    {
                        Ports =
                        [
                            new CiliumPort { Port = "53", Protocol = "UDP" },
                            new CiliumPort { Port = "53", Protocol = "TCP" }
                        ]
                    }
                ]
            },
            // K8s API server egress
            new()
            {
                ToEntities = ["kube-apiserver"],
                ToPorts =
                [
                    new CiliumPortRule
                    {
                        Ports = [new CiliumPort { Port = "443", Protocol = "TCP" }]
                    }
                ]
            }
        };

        return new GenericCiliumNetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{name}-netpol",
                NamespaceProperty = ns,
                OwnerReferences = owner != null ? [owner] : null
            },
            Spec = new CiliumNetworkPolicySpec
            {
                EndpointSelector = new CiliumEndpointSelector { MatchLabels = labels },
                Ingress = ingress,
                Egress = egress
            }
        };
    }

    public GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null)
    {
        var policy = BuildForInstance(name, ns, labels, sqlPort, owner, allowedNamespaces, operatorNamespace);

        // Add AG mirroring port for inter-replica communication
        policy.Spec.Ingress!.Add(new CiliumIngressRule
        {
            FromEndpoints = [new CiliumEndpointSelector { MatchLabels = labels }],
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

    private static void AddNamespaceIngressRules(List<CiliumIngressRule> ingress, List<string>? allowedNamespaces, string? operatorNamespace, int sqlPort)
    {
        var namespaces = new HashSet<string>();
        if (allowedNamespaces != null)
            foreach (var ns in allowedNamespaces) namespaces.Add(ns);
        if (operatorNamespace != null)
            namespaces.Add(operatorNamespace);

        foreach (var ns in namespaces)
        {
            ingress.Add(new CiliumIngressRule
            {
                FromEndpoints =
                [
                    new CiliumEndpointSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            ["k8s:io.kubernetes.pod.namespace"] = ns
                        }
                    }
                ],
                ToPorts =
                [
                    new CiliumPortRule
                    {
                        Ports = [new CiliumPort { Port = sqlPort.ToString(), Protocol = "TCP" }]
                    }
                ]
            });
        }
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
    public List<string>? ToEntities { get; set; }
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
