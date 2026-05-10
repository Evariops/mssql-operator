namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlConfigurationSpec
{
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
}

public class AdvancedConfigParam
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
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
    public string? DefaultDataPath { get; set; }
    public string? DefaultLogPath { get; set; }
    public string? DefaultBackupPath { get; set; }
    public int? ErrorLogSizeKb { get; set; }
    public int? NumErrorLogs { get; set; }
    public string AuthenticationMode { get; set; } = "Mixed";
}
