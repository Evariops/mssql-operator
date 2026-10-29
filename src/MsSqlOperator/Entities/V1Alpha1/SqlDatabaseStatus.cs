using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlDatabaseStatus
{
    /// <summary>Pending | Creating | Joining | Ready | Conflict | Failed | Deleting.</summary>
    public string Phase { get; set; } = Phases.Pending;

    public long ObservedGeneration { get; set; }

    /// <summary>The server the database was last seen on: the instance, or the AG primary.</summary>
    public string? Server { get; set; }

    /// <summary>True when the database existed before this resource and was adopted.</summary>
    public bool Adopted { get; set; }

    /// <summary>When the operator created the database, or adopted it.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>The options as read on the server at the last pass.</summary>
    public string? Collation { get; set; }
    public string? RecoveryModel { get; set; }
    public int? CompatibilityLevel { get; set; }
    public string? Owner { get; set; }

    /// <summary>The SqlAvailabilityGroupDatabase managed for this database, and its phase.</summary>
    public string? AvailabilityGroupDatabase { get; set; }
    public string? AvailabilityGroupPhase { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
