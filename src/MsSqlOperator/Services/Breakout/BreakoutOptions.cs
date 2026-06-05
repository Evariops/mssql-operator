namespace MsSqlOperator.Services.Breakout;

public class BreakoutOptions
{
    public const string SectionName = "FailoverBreakout";

    public bool Enabled { get; set; }
    public int ControlLoopPeriodSeconds { get; set; } = 5;
    public int TerminatingThresholdSeconds { get; set; } = 30;
    public int NodeReadyGracePeriodSeconds { get; set; } = 60;
    public string PodSelector { get; set; } = "mssql.operator/instance";
    public string PodSelectorAg { get; set; } = "mssql.operator/ag";
    public string FencingDriver { get; set; } = "volumeattachment";
    public int MaxConcurrentFencing { get; set; } = 3;
    public string BlacklistConfigMapName { get; set; } = "mssql-failover-blacklist";
    public string BlacklistConfigMapNamespace { get; set; } = "default";
}
