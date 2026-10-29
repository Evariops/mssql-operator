using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlDatabaseSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public AutomationSpec? Automation { get; set; }

    /// <summary>The SqlInstance holding the database (same namespace). Exactly one of
    /// instanceRef and availabilityGroupRef. Immutable.</summary>
    public ResourceReference? InstanceRef { get; set; }

    /// <summary>The SqlAvailabilityGroup holding the database (same namespace): it is created on the
    /// primary and joined to the AG. Exactly one of instanceRef and availabilityGroupRef. Immutable.</summary>
    public ResourceReference? AvailabilityGroupRef { get; set; }

    /// <summary>Name of the database. Immutable.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Collation, applied at creation only (the server's default otherwise). Immutable: a
    /// database cannot change collation in place.</summary>
    public string? Collation { get; set; }

    /// <summary>Full | Simple | BulkLogged. Unset: left as it is (FULL at creation, from model).
    /// A database in an availability group is always Full.</summary>
    public string? RecoveryModel { get; set; }

    /// <summary>Compatibility level (100 to 170). Unset: left as it is (the server's at creation).</summary>
    public int? CompatibilityLevel { get; set; }

    /// <summary>Login owning the database (ALTER AUTHORIZATION). Unset: left as it is (sa at creation).</summary>
    public string? Owner { get; set; }

    /// <summary>Size and growth of the primary data file and of the log file.</summary>
    public SqlDatabaseFilesSpec? Files { get; set; }

    /// <summary>Retain (default): deleting the resource keeps the database (and its AG membership).
    /// Delete: deleting the resource drops the database, on every replica of an AG.</summary>
    public string DeletionPolicy { get; set; } = "Retain";

    /// <summary>How the database joins its availability group: applied to the
    /// SqlAvailabilityGroupDatabase the operator manages. Only the fields set here are enforced.</summary>
    public SqlDatabaseAvailabilityGroupSpec? AvailabilityGroup { get; set; }
}

public class SqlDatabaseFilesSpec
{
    /// <summary>The primary data file.</summary>
    public SqlDatabaseFileSpec? Data { get; set; }

    /// <summary>The log file.</summary>
    public SqlDatabaseFileSpec? Log { get; set; }
}

public class SqlDatabaseFileSpec
{
    /// <summary>Size (quantity, e.g. 1Gi). A file only grows: a smaller size than the current one is
    /// ignored.</summary>
    public string? Size { get; set; }

    /// <summary>Growth increment: a quantity (256Mi) or a percentage (10%).</summary>
    public string? Growth { get; set; }

    /// <summary>Maximum size: a quantity, or Unlimited.</summary>
    public string? MaxSize { get; set; }
}

public class SqlDatabaseAvailabilityGroupSpec
{
    /// <summary>Automatic | Manual. Defaults to Automatic.</summary>
    public string? SeedingMode { get; set; }

    /// <summary>Log backups the operator takes when nothing else truncates the log.</summary>
    public AgDatabaseLogBackupSpec? LogBackup { get; set; }

    /// <summary>When the initialization backup may run.</summary>
    public AgDatabaseInitializationSpec? Initialization { get; set; }

    /// <summary>Data movement control (suspend/resume).</summary>
    public AgDatabaseDataMovementSpec? DataMovement { get; set; }
}
