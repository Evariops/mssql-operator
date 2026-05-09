using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAgentJobStatus
{
    public string? JobId { get; set; }
    public bool Created { get; set; }
    public DateTime? LastRunDate { get; set; }
    public string? LastRunStatus { get; set; }
    public DateTime? NextRunDate { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}
