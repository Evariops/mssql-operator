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
}
