namespace MsSqlOperator.Entities.Common;

public class DatabaseFilterSpec
{
    public string? Pattern { get; set; }
    public List<string>? Exclude { get; set; }
    public Dictionary<string, string>? MatchLabels { get; set; }
}
