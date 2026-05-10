using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAgentJobStatus
{
    public bool Created { get; set; }
    public List<AgentJobTargetStatus>? TargetStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class AgentJobTargetStatus
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public bool Created { get; set; }
    public DateTime? LastRunDate { get; set; }
    public string? LastRunStatus { get; set; }
    public DateTime? NextRunDate { get; set; }
    public string? LastError { get; set; }
}
