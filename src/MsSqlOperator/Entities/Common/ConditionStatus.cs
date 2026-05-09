namespace MsSqlOperator.Entities.Common;

public class ConditionStatus
{
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "False";
    public string? Reason { get; set; }
    public string? Message { get; set; }
    public DateTime? LastTransitionTime { get; set; }
}
