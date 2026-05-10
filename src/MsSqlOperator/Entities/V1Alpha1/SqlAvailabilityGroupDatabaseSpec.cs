using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupDatabaseSpec
{
    /// <summary>Reference to the parent SqlAvailabilityGroup (same namespace).</summary>
    public ResourceReference AvailabilityGroupRef { get; set; } = new();

    /// <summary>Name of the database. Must already exist on the primary, be ONLINE, FULL recovery.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Automatic | Manual. Defaults to Automatic.</summary>
    public string SeedingMode { get; set; } = "Automatic";

    /// <summary>Data movement control (suspend/resume).</summary>
    public AgDatabaseDataMovementSpec? DataMovement { get; set; }
}

public class AgDatabaseDataMovementSpec
{
    /// <summary>If true, the operator executes ALTER DATABASE SET HADR SUSPEND.</summary>
    public bool Suspended { get; set; }

    /// <summary>If true, block automatic HADR RESUME after a forced failover with data loss.</summary>
    public bool RequireManualResumeAfterDataLoss { get; set; } = true;
}
