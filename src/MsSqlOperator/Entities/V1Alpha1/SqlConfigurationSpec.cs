namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlConfigurationSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public MsSqlOperator.Entities.Common.AutomationSpec? Automation { get; set; }

    public Dictionary<string, string>? TargetMatchLabels { get; set; }
    public SpConfigureSpec? SpConfigure { get; set; }
    public TraceFlagSpec? TraceFlags { get; set; }
    public TempDbSpec? TempDb { get; set; }
    public ModelDatabaseSpec? ModelDatabase { get; set; }
    public ServerSettingsSpec? ServerSettings { get; set; }
    public DatabaseMailSpec? DatabaseMail { get; set; }
    public List<AgentOperatorSpec>? AgentOperators { get; set; }
}

public class SpConfigureSpec
{
    public int? MaxServerMemoryMb { get; set; }
    public int? MinServerMemoryMb { get; set; }
    public int? MaxDegreeOfParallelism { get; set; }
    public int? CostThresholdForParallelism { get; set; }
    public bool? OptimizeForAdHocWorkloads { get; set; }
    public bool? RemoteAdminConnections { get; set; }
    public bool? BackupCompressionDefault { get; set; }
    public bool? DefaultBackupChecksum { get; set; }
    public bool? ContainedDatabaseAuthentication { get; set; }
    public bool? ClrEnabled { get; set; }
    public bool? CrossDbOwnershipChaining { get; set; }
    public List<AdvancedConfigParam>? Advanced { get; set; }

    /// <summary>The typed toggles that map to dangerous sp_configure options
    /// (clrEnabled, crossDbOwnershipChaining, remoteAdminConnections) enable code execution or
    /// cross a trust boundary. Setting any of them to <c>true</c> requires this explicit,
    /// review-visible opt-in — mirroring <see cref="AdvancedConfigParam.AllowDangerous"/> for the
    /// open-ended advanced[] list. Disabling (false) never requires it.</summary>
    public bool AllowDangerous { get; set; }
}

public class AdvancedConfigParam
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }

    /// <summary>Dangerous sp_configure options (xp_cmdshell,
    /// clr enabled, …) require this explicit, review-visible opt-in.</summary>
    public bool AllowDangerous { get; set; }
}

public class TraceFlagSpec
{
    public List<int>? Global { get; set; }
    public List<int>? Startup { get; set; }
}

public class TempDbSpec
{
    public int DataFiles { get; set; } = 8;
    public int InitialSizeMb { get; set; } = 1024;
    public int AutoGrowthMb { get; set; } = 512;
    public int LogInitialSizeMb { get; set; } = 256;
    public int LogAutoGrowthMb { get; set; } = 256;
}

public class ModelDatabaseSpec
{
    public string RecoveryModel { get; set; } = "Simple";
    public int AutoGrowthMb { get; set; } = 256;
}

public class ServerSettingsSpec
{
    // The paths are written into mssql.conf as is — a control character (a line break)
    // would start a new setting. Enforced by the schema too, for when the webhook is not called.
    private const string MssqlPath = "^/var/opt/mssql/[^\\x00-\\x1f\\x7f]*$";

    [System.ComponentModel.DataAnnotations.RegularExpression(MssqlPath)]
    public string? DefaultDataPath { get; set; }
    [System.ComponentModel.DataAnnotations.RegularExpression(MssqlPath)]
    public string? DefaultLogPath { get; set; }
    [System.ComponentModel.DataAnnotations.RegularExpression(MssqlPath)]
    public string? DefaultBackupPath { get; set; }
    public int? ErrorLogSizeKb { get; set; }
    public int? NumErrorLogs { get; set; }
    public string AuthenticationMode { get; set; } = "Mixed";
}
