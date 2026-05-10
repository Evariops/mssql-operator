namespace MsSqlOperator.Entities.Common;

public class TargetSelector
{
    public List<string>? Namespaces { get; set; }
    public Dictionary<string, string>? MatchLabels { get; set; }
    public List<string>? Instances { get; set; }
    public List<string>? AvailabilityGroups { get; set; }
}
