namespace MsSqlOperator.Entities.Common;

public class NetworkPolicySpec
{
    public bool Enabled { get; set; }
    public List<string>? AllowedNamespaces { get; set; }
    public string? OperatorNamespace { get; set; }
}
