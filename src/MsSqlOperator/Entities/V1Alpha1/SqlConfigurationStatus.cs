using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlConfigurationStatus
{
    public List<AppliedInstance>? AppliedTo { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class AppliedInstance
{
    public string InstanceName { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public DateTime? LastApplied { get; set; }
}
