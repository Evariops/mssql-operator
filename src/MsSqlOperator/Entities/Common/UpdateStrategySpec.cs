using System.ComponentModel.DataAnnotations;

namespace MsSqlOperator.Entities.Common;

public class UpdateStrategySpec
{
    [RegularExpression("^(RollingUpdate|OnDelete)$")]
    public string Type { get; set; } = "RollingUpdate";
    public RollingUpdateSpec? RollingUpdate { get; set; }
}

public class RollingUpdateSpec
{
    // maxUnavailable=0 would block the rollout forever; require >= 1.
    [Range(1, 9)]
    public int MaxUnavailable { get; set; } = 1;
    /// <summary>Not implemented: no backup is taken before a replica restarts. Admission refuses true.</summary>
    public bool PreUpdateBackup { get; set; }
    public int HealthCheckTimeoutSeconds { get; set; } = 300;
    public int PauseBetweenPodsSeconds { get; set; } = 60;
    public bool UpdateSecondariesFirst { get; set; } = true;
    public bool FailoverBeforePrimaryUpdate { get; set; } = true;
}
