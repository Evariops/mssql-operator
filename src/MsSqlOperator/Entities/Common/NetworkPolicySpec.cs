namespace MsSqlOperator.Entities.Common;

public class NetworkPolicySpec
{
    public bool Enabled { get; set; }
    public List<string>? AllowedNamespaces { get; set; }
    /// <summary>Deprecated — the operator namespace is sourced from the operator's
    /// own POD_NAMESPACE env var, not from each user CR (a wrong value here cut the operator
    /// off from its instance). Ignored by the controller.</summary>
    [System.Obsolete("Sourced from the operator's POD_NAMESPACE; ignored.")]
    public string? OperatorNamespace { get; set; }
    /// <summary>Destinations the SQL pods may reach besides DNS and the other replicas —
    /// an SMTP relay (Database Mail), object storage (BACKUP TO URL), linked servers. Without a rule,
    /// the policy lets nothing else out.</summary>
    public List<NetworkEgressRule>? Egress { get; set; }
}

/// <summary>One egress destination of the SQL pods. Give exactly one kind of destination:
/// DNS names, CIDR blocks, or in-cluster pods (namespace and/or labels).</summary>
public class NetworkEgressRule
{
    /// <summary>What the rule is for (smtp, backups, a linked server) — documentation only.</summary>
    public string? Name { get; set; }
    /// <summary>DNS names (Cilium toFQDNs); a leading <c>*.</c> matches the subdomains.</summary>
    public List<string>? Fqdns { get; set; }
    /// <summary>CIDR blocks outside the cluster.</summary>
    public List<string>? Cidrs { get; set; }
    /// <summary>In-cluster pods of this namespace (the instance's own when only labels are given).</summary>
    public string? Namespace { get; set; }
    /// <summary>In-cluster pods carrying these labels.</summary>
    public Dictionary<string, string>? PodLabels { get; set; }
    /// <summary>TCP ports allowed to the destination (1–65535).</summary>
    public List<int> Ports { get; set; } = [];
}
