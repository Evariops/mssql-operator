namespace MsSqlOperator.Services.Breakout;

public class BreakoutOptions
{
    public const string SectionName = "FailoverBreakout";

    public bool Enabled { get; set; }
    public int ControlLoopPeriodSeconds { get; set; } = 5;
    public int TerminatingThresholdSeconds { get; set; } = 30;
    public int NodeReadyGracePeriodSeconds { get; set; } = 60;
    public string PodSelector { get; set; } = "mssql-operator.evariops.com/instance";
    public string PodSelectorAg { get; set; } = "mssql-operator.evariops.com/ag";
    public string FencingDriver { get; set; } = "volumeattachment";
    public int MaxConcurrentFencing { get; set; } = 3;
    public string BlacklistConfigMapName { get; set; } = "mssql-failover-blacklist";
    public string BlacklistConfigMapNamespace { get; set; } = "default";

    /// <summary>
    /// Gate the control loop behind a Lease so that, if the breakout deployment is scaled or rolled,
    /// only one replica ever fences. Also makes the single-writer assumption on the
    /// blacklist ConfigMap a guarantee rather than a default.
    /// </summary>
    public bool LeaderElection { get; set; } = true;

    public string LeaseName { get; set; } = "mssql-failover-breakout-leader";

    /// <summary>30 s / 20 s / 2 s, and the control loop gets <see cref="LostLeadershipStopSeconds"/>
    /// to stop once the Lease is lost, so it never fences while another replica holds it. Checked at
    /// startup.</summary>
    public int LeaseDurationSeconds { get; set; } = 30;
    public int LeaseRenewDeadlineSeconds { get; set; } = 20;
    public int LeaseRetryPeriodSeconds { get; set; } = 2;

    /// <summary>How long the control loop has to stop after a leadership loss; past it the
    /// process exits rather than risk fencing next to the new leader.</summary>
    public int LostLeadershipStopSeconds { get; set; } = 5;

    /// <summary>Refuses lease timings under which two breakout leaders could overlap.</summary>
    public void EnsureSafeLeaseTiming()
        => MsSqlOperator.Operator.Registration.LeaseTiming.EnsureSafe(LeaseName, TimeSpan.FromSeconds(LeaseDurationSeconds),
            TimeSpan.FromSeconds(LeaseRenewDeadlineSeconds), TimeSpan.FromSeconds(LeaseRetryPeriodSeconds),
            TimeSpan.FromSeconds(LostLeadershipStopSeconds));

    /// <summary>
    /// Only fencings within this trailing window count toward <see cref="MaxConcurrentFencing"/>.
    /// Bounds the fencing <em>rate</em> instead of the historical blacklist, so nodes stuck NotReady
    /// (hardware-dead, never returning) don't permanently saturate the limit and block new fencing.
    /// </summary>
    public int FencingRateWindowSeconds { get; set; } = 300;
}
