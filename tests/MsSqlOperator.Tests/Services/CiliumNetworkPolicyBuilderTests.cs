using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Services;

/// <summary>Egress declared in the spec, ingress from the operator's pods only.</summary>
public class CiliumNetworkPolicyBuilderTests
{
    private static readonly Dictionary<string, string> Labels = new() { ["mssql-operator.evariops.com/instance"] = "sql" };
    private static readonly Dictionary<string, string> OperatorPods = new() { ["app.kubernetes.io/component"] = "operator" };

    private static GenericCiliumNetworkPolicy Build(NetworkPolicySpec? spec = null, string? operatorNamespace = "ops",
        IReadOnlyDictionary<string, string>? operatorPods = null)
        => new CiliumNetworkPolicyBuilder().BuildForInstance("sql", "app", Labels, 1433, null, spec, operatorNamespace, operatorPods ?? OperatorPods);

    [Fact]
    public void SqlPods_NoLongerReachTheApiServer()
        => Assert.DoesNotContain(Build().Spec.Egress!, r => r.ToEntities?.Contains("kube-apiserver") == true);

    [Fact]
    public void FromTheOperatorNamespace_OnlyTheOperatorPodsReachSql()
    {
        var rule = Assert.Single(Build().Spec.Ingress!, r => r.FromEndpoints?.Single().MatchLabels?.ContainsKey("k8s:io.kubernetes.pod.namespace") == true);

        var match = rule.FromEndpoints!.Single().MatchLabels!;
        Assert.Equal("ops", match["k8s:io.kubernetes.pod.namespace"]);
        Assert.Equal("operator", match["app.kubernetes.io/component"]);
    }

    [Fact]
    public void OperatorNamespaceAlsoAllowedByTheUser_StaysOpenAsAWhole()
    {
        var rule = Assert.Single(Build(new NetworkPolicySpec { AllowedNamespaces = ["ops"] }).Spec.Ingress!,
            r => r.FromEndpoints?.Single().MatchLabels?.ContainsKey("k8s:io.kubernetes.pod.namespace") == true);

        Assert.Single(rule.FromEndpoints!.Single().MatchLabels!);
    }

    [Fact]
    public void DeclaredEgress_BecomesOneRulePerDestination()
    {
        var policy = Build(new NetworkPolicySpec
        {
            Egress =
            [
                new NetworkEgressRule { Name = "smtp", Fqdns = ["smtp.example.com", "*.mail.example.com"], Ports = [587] },
                new NetworkEgressRule { Name = "linked", Cidrs = ["10.20.0.0/16"], Ports = [1433] },
                new NetworkEgressRule { Name = "minio", Namespace = "storage", PodLabels = new() { ["app"] = "minio" }, Ports = [9000] },
            ],
        });
        var egress = policy.Spec.Egress!;

        var fqdn = Assert.Single(egress, r => r.ToFqdns is not null);
        Assert.Equal("smtp.example.com", fqdn.ToFqdns![0].MatchName);
        Assert.Equal("*.mail.example.com", fqdn.ToFqdns[1].MatchPattern);
        Assert.Equal(["10.20.0.0/16"], Assert.Single(egress, r => r.ToCidr is not null).ToCidr!);
        var pods = Assert.Single(egress, r => r.ToEndpoints?.Single().MatchLabels?.GetValueOrDefault("app") == "minio");
        Assert.Equal("storage", pods.ToEndpoints!.Single().MatchLabels!["k8s:io.kubernetes.pod.namespace"]);
        Assert.Equal("9000", pods.ToPorts!.Single().Ports!.Single().Port);
        // toFQDNs only matches what the DNS proxy saw: the DNS rule carries an L7 rule.
        Assert.NotNull(egress[0].ToPorts!.Single().Rules?.Dns);
    }

    [Fact]
    public void WithoutFqdns_DnsHasNoL7Rule()
        => Assert.Null(Build().Spec.Egress![0].ToPorts!.Single().Rules);

    [Fact]
    public void Serialized_UsesTheCiliumFieldNames()
    {
        var json = k8s.KubernetesJson.Serialize(Build(new NetworkPolicySpec
        {
            Egress = [new NetworkEgressRule { Fqdns = ["smtp.example.com"], Ports = [25] }, new NetworkEgressRule { Cidrs = ["10.0.0.0/8"], Ports = [1433] }],
        }));

        Assert.Contains("\"toFQDNs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"toCIDR\"", json, StringComparison.Ordinal);
        Assert.Contains("\"matchName\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null, 587, "exactly one kind")]
    [InlineData("smtp.example.com", "10.0.0.0/8", null, 587, "exactly one kind")]
    [InlineData("smtp.example.com", null, null, 0, "ports")]
    [InlineData(null, "10.0.0.0/33", null, 1433, "not a CIDR")]
    [InlineData("smtp.*.example.com", null, null, 25, "not a DNS name")]
    [InlineData(null, null, "storage", 9000, null)]
    [InlineData("*.example.com", null, null, 443, null)]
    public void Validator_RefusesRulesThePolicyCouldNotCarry(string? fqdn, string? cidr, string? ns, int port, string? expected)
    {
        var error = NetworkPolicyValidator.Validate(new NetworkPolicySpec
        {
            Egress = [new NetworkEgressRule
            {
                Fqdns = fqdn is null ? null : [fqdn], Cidrs = cidr is null ? null : [cidr], Namespace = ns, Ports = [port],
            }],
        });

        if (expected is null) Assert.Null(error);
        else Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app.kubernetes.io/name=mssql-operator,app.kubernetes.io/component=operator", 2)]
    [InlineData("", 0)]
    [InlineData("app.kubernetes.io/name", 0)]
    public void OperatorPodSelector_IsParsed_MalformedFallsBackToTheNamespace(string selector, int count)
        => Assert.Equal(count, new OperatorOptions { OperatorPodSelector = selector }.OperatorPodLabels().Count);
}
