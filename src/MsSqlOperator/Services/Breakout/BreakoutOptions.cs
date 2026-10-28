namespace MsSqlOperator.Services.Breakout;

public class BreakoutOptions
{
    public const string SectionName = "FailoverBreakout";

    public bool Enabled { get; set; }
    /// <summary>The safety net of the breakout loop, woken otherwise by the watch of the pods
    /// in Terminating; it also lets relocationDelay elapse.</summary>
    public int ControlLoopPeriodSeconds { get; set; } = 10;
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

    /// <summary>
    /// How long the node of a stuck pod must stay NotReady, its Lease unchanged (measured by
    /// the operator's clock), before the pod is relocated. Kubernetes never recreates a StatefulSet
    /// pod stuck on an unreachable node by itself.
    /// </summary>
    public int RelocationDelaySeconds { get; set; } = 600;

    /// <summary>
    /// The CSI drivers whose detachment really withdraws the node's access on the
    /// storage side. A pod is relocated only when every one of its volumes uses one of them; any other
    /// (or node-local storage) is a human's call — NeedsAttention InstanceOnLostNode / ReplicaOnLostNode.
    /// </summary>
    public List<string> TrustedFencingCsiDrivers { get; set; } = [];

    /// <summary>Upper bound on one pass of the control loop. An API call that hangs held the
    /// loop — and every fencing decision behind it — indefinitely; the pass is now cut short and the
    /// next one starts over.</summary>
    public int PassDeadlineSeconds { get; set; } = 30;

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
