using k8s;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

public interface ICiliumNetworkPolicyBuilder
{
    GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner,
        NetworkPolicySpec? spec = null, string? operatorNamespace = null, IReadOnlyDictionary<string, string>? operatorPodLabels = null);
    GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner,
        NetworkPolicySpec? spec = null, string? operatorNamespace = null, IReadOnlyDictionary<string, string>? operatorPodLabels = null);
}

public class CiliumNetworkPolicyBuilder : ICiliumNetworkPolicyBuilder
{
    private const string NamespaceLabel = "k8s:io.kubernetes.pod.namespace";

    public GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner,
        NetworkPolicySpec? spec = null, string? operatorNamespace = null, IReadOnlyDictionary<string, string>? operatorPodLabels = null)
    {
        var ingress = new List<CiliumIngressRule>
        {
            // Same namespace ingress
            new()
            {
                FromEndpoints = [new CiliumEndpointSelector()],
                ToPorts = [TcpPorts(sqlPort)]
            }
        };

        // Additional namespace ingress rules
        AddNamespaceIngressRules(ingress, spec?.AllowedNamespaces, operatorNamespace, operatorPodLabels, sqlPort);

        var fqdns = spec?.Egress?.Any(r => r.Fqdns is { Count: > 0 }) == true;
        var egress = new List<CiliumEgressRule>
        {
            // DNS egress — restricted to kube-dns (ToEndpoints [{}] allowed egress
            // to EVERY pod in the cluster on 53, which is not "DNS only").
            new()
            {
                ToEndpoints =
                [
                    new CiliumEndpointSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            [NamespaceLabel] = "kube-system",
                            ["k8s-app"] = "kube-dns",
                        }
                    }
                ],
                ToPorts =
                [
                    new CiliumPortRule
                    {
                        Ports =
                        [
                            new CiliumPort { Port = "53", Protocol = "UDP" },
                            new CiliumPort { Port = "53", Protocol = "TCP" }
                        ],
                        // toFQDNs only matches names the DNS proxy saw resolved.
                        Rules = fqdns ? new CiliumL7Rules { Dns = [new CiliumDnsRule { MatchPattern = "*" }] } : null
                    }
                ]
            }
            // No egress to the API server any more — nothing in the SQL pods calls it.
        };
        foreach (var rule in spec?.Egress ?? [])
            if (EgressRule(rule) is { } built)
                egress.Add(built);

        return new GenericCiliumNetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{name}-netpol",
                NamespaceProperty = ns,
                // Scoping label so the owned-resource watch selector covers the netpol.
                Labels = new Dictionary<string, string>(labels) { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
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
        NetworkPolicySpec? spec = null, string? operatorNamespace = null, IReadOnlyDictionary<string, string>? operatorPodLabels = null)
    {
        var policy = BuildForInstance(name, ns, labels, sqlPort, owner, spec, operatorNamespace, operatorPodLabels);

        // Add AG mirroring port for inter-replica communication
        policy.Spec.Ingress!.Add(new CiliumIngressRule
        {
            FromEndpoints = [new CiliumEndpointSelector { MatchLabels = labels }],
            ToPorts = [TcpPorts(mirroringPort)]
        });

        // Allow egress to other AG replicas on mirroring port
        policy.Spec.Egress!.Add(new CiliumEgressRule
        {
            ToEndpoints = [new CiliumEndpointSelector { MatchLabels = labels }],
            ToPorts = [TcpPorts(mirroringPort, sqlPort)]
        });

        return policy;
    }

    /// <summary>One declared destination; null for a rule admission would have refused.</summary>
    internal static CiliumEgressRule? EgressRule(NetworkEgressRule rule)
    {
        if (rule.Ports is not { Count: > 0 })
            return null;
        var ports = TcpPorts([.. rule.Ports]);
        if (rule.Fqdns is { Count: > 0 } fqdns)
            return new CiliumEgressRule
            {
                ToFqdns = fqdns.Select(f => f.StartsWith("*.", StringComparison.Ordinal)
                    ? new CiliumFqdnSelector { MatchPattern = f }
                    : new CiliumFqdnSelector { MatchName = f }).ToList(),
                ToPorts = [ports]
            };
        if (rule.Cidrs is { Count: > 0 } cidrs)
            return new CiliumEgressRule { ToCidr = [.. cidrs], ToPorts = [ports] };
        if (rule.Namespace is { Length: > 0 } || rule.PodLabels is { Count: > 0 })
        {
            var match = new Dictionary<string, string>(rule.PodLabels ?? new Dictionary<string, string>());
            if (rule.Namespace is { Length: > 0 } targetNs)
                match[NamespaceLabel] = targetNs;
            return new CiliumEgressRule { ToEndpoints = [new CiliumEndpointSelector { MatchLabels = match }], ToPorts = [ports] };
        }
        return null;
    }

    private static CiliumPortRule TcpPorts(params int[] ports)
        => new() { Ports = ports.Select(p => new CiliumPort { Port = p.ToString(System.Globalization.CultureInfo.InvariantCulture), Protocol = "TCP" }).ToList() };

    private static void AddNamespaceIngressRules(List<CiliumIngressRule> ingress, List<string>? allowedNamespaces, string? operatorNamespace,
        IReadOnlyDictionary<string, string>? operatorPodLabels, int sqlPort)
    {
        var namespaces = new HashSet<string>(allowedNamespaces ?? []);
        foreach (var ns in namespaces)
            ingress.Add(new CiliumIngressRule
            {
                FromEndpoints = [new CiliumEndpointSelector { MatchLabels = new Dictionary<string, string> { [NamespaceLabel] = ns } }],
                ToPorts = [TcpPorts(sqlPort)]
            });

        // From the operator namespace, the operator's pods only — every other pod of that
        // namespace reached 1433 too. A namespace the user allowed stays open as a whole.
        if (operatorNamespace is null || namespaces.Contains(operatorNamespace))
            return;
        var match = new Dictionary<string, string>(operatorPodLabels ?? new Dictionary<string, string>()) { [NamespaceLabel] = operatorNamespace };
        ingress.Add(new CiliumIngressRule
        {
            FromEndpoints = [new CiliumEndpointSelector { MatchLabels = match }],
            ToPorts = [TcpPorts(sqlPort)]
        });
    }
}

// Cilium CRD types (not part of k8s client, defined as generic objects)
[KubernetesEntity(Group = "cilium.io", ApiVersion = "v2", Kind = "CiliumNetworkPolicy", PluralName = "ciliumnetworkpolicies")]
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
    [System.Text.Json.Serialization.JsonPropertyName("toCIDR")]
    public List<string>? ToCidr { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("toFQDNs")]
    public List<CiliumFqdnSelector>? ToFqdns { get; set; }
    public List<CiliumPortRule>? ToPorts { get; set; }
}

public class CiliumFqdnSelector
{
    public string? MatchName { get; set; }
    public string? MatchPattern { get; set; }
}

public class CiliumPortRule
{
    public List<CiliumPort>? Ports { get; set; }
    public CiliumL7Rules? Rules { get; set; }
}

public class CiliumL7Rules
{
    public List<CiliumDnsRule>? Dns { get; set; }
}

public class CiliumDnsRule
{
    public string? MatchPattern { get; set; }
}

public class CiliumPort
{
    public string Port { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
}
