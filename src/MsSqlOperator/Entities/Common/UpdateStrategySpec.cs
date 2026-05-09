namespace MsSqlOperator.Entities.Common;

public class UpdateStrategySpec
{
    public string Type { get; set; } = "RollingUpdate";
    public RollingUpdateSpec? RollingUpdate { get; set; }
}

public class RollingUpdateSpec
{
    public int MaxUnavailable { get; set; } = 1;
    public bool PreUpdateBackup { get; set; } = true;
    public int HealthCheckTimeoutSeconds { get; set; } = 300;
    public int PauseBetweenPodsSeconds { get; set; } = 60;
    public bool UpdateSecondariesFirst { get; set; } = true;
    public bool FailoverBeforePrimaryUpdate { get; set; } = true;
}
