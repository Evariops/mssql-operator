namespace MsSqlOperator.Entities.Common;

/// <summary>
/// Selects the SqlInstances/SqlAvailabilityGroups a CR applies to. Resolution is always
/// confined to the CR's own namespace: cross-namespace
/// targeting was removed — it allowed any namespace tenant to act on another tenant's
/// instances, bypassing Kubernetes RBAC.
/// </summary>
public class TargetSelector
{
    public Dictionary<string, string>? MatchLabels { get; set; }
    public List<string>? Instances { get; set; }
    public List<string>? AvailabilityGroups { get; set; }
}
