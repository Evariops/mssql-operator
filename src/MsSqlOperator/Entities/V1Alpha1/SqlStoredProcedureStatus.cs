using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlStoredProcedureStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? Source { get; set; }
    public string? SourceRef { get; set; }
    public List<StoredProcedureTargetStatus>? TargetStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class StoredProcedureTargetStatus
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public List<DeployedDatabaseProcedures>? DeployedProcedures { get; set; }
    public string? LastError { get; set; }
}

public class DeployedDatabaseProcedures
{
    public string Database { get; set; } = string.Empty;
    public List<DeployedProcedureInfo>? Procedures { get; set; }
}

public class DeployedProcedureInfo
{
    public string Name { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
    public bool Deployed { get; set; }
    public string? Hash { get; set; }
    public DateTime? LastDeployed { get; set; }
}
