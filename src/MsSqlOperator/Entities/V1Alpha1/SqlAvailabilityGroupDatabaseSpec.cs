using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupDatabaseSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public MsSqlOperator.Entities.Common.AutomationSpec? Automation { get; set; }

    /// <summary>Reference to the parent SqlAvailabilityGroup (same namespace).</summary>
    public ResourceReference AvailabilityGroupRef { get; set; } = new();

    /// <summary>Name of the database. Must already exist on the primary, be ONLINE, FULL recovery.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Automatic | Manual. Defaults to Automatic.</summary>
    public string SeedingMode { get; set; } = "Automatic";

    /// <summary>Data movement control (suspend/resume).</summary>
    public AgDatabaseDataMovementSpec? DataMovement { get; set; }

    /// <summary>Log backups the operator takes when nothing else truncates the log.</summary>
    public AgDatabaseLogBackupSpec? LogBackup { get; set; }

    /// <summary>How the database joins the AG.</summary>
    public AgDatabaseInitializationSpec? Initialization { get; set; }
}

public class AgDatabaseInitializationSpec
{
    /// <summary>When set, the initialization backup (taken when the database has no log
    /// chain) starts only inside this window — a full backup reads the whole database on the primary.
    /// The database joins the AG once it is taken.</summary>
    public MaintenanceWindowSpec? BackupWindow { get; set; }
}

public class AgDatabaseLogBackupSpec
{
    /// <summary>Who holds the log chain. <c>Local</c>: the operator backs up the log when it
    /// is at least <see cref="ThresholdPercent"/> full and nothing else truncated it (log_reuse_wait
    /// LOG_BACKUP), into its folder on the primary — so it never acts while another tool backs the log
    /// up often enough. <c>Disabled</c>: never, your tooling owns the log backups. <c>ObjectStorage</c>:
    /// not available yet. Unset, or <c>Auto</c> (deprecated): <c>Local</c> with the purge after 24 h,
    /// for one more version; the default then becomes <c>Disabled</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Log usage (percent) from which the operator backs the log up. Defaults to 50.</summary>
    public int ThresholdPercent { get; set; } = 50;

    /// <summary>The operator's log backups older than this are deleted from its folder on the replica
    /// that took them. In <c>Local</c> mode, only when set — the files are part of the log
    /// chain, and a restore needs them. Unset mode or <c>Auto</c>: 24 hours.</summary>
    public int? RetentionHours { get; set; }
}

public class AgDatabaseDataMovementSpec
{
    /// <summary>If true, the operator executes ALTER DATABASE SET HADR SUSPEND.</summary>
    public bool Suspended { get; set; }

    /// <summary>If true, block automatic HADR RESUME after a forced failover with data loss.</summary>
    public bool RequireManualResumeAfterDataLoss { get; set; } = true;
}
