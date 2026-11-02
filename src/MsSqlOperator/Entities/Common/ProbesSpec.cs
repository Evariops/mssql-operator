using System.ComponentModel.DataAnnotations;

namespace MsSqlOperator.Entities.Common;

/// <summary>
/// Probe budgets for the SQL Server container.
///
/// The defaults used to be strict — a 5-second readiness timeout with 3 failures — so an instance
/// that was merely slow (a saturated primary, a checkpoint storm, a full-text population) was pulled
/// out of its Service after 15 seconds. Removing the only endpoint from an overloaded-but-working
/// database turns a slowdown into an outage, and the operator's own health probes then see nothing
/// at all. The defaults here are deliberately tolerant, and every budget is now configurable for
/// instances under heavy load.
/// </summary>
public class ProbesSpec
{
    /// <summary>Consecutive readiness failures before the pod leaves the Service. Default 6 (≈1 min).</summary>
    [Range(1, 60)]
    public int ReadinessFailureThreshold { get; set; } = 6;

    /// <summary>Readiness probe timeout, also used as the sqlcmd login timeout (-l). Default 10s.</summary>
    [Range(1, 60)]
    public int ReadinessTimeoutSeconds { get; set; } = 10;

    /// <summary>Startup probe failures (10s period) before the container is restarted. Default 90 (15 min),
    /// which is the budget for the master upgrade scripts of a cumulative update.</summary>
    [Range(1, 720)]
    public int StartupFailureThreshold { get; set; } = 90;

    /// <summary>Startup probe period, in seconds. Default 10. A shorter one sees SQL Server
    /// accept connections sooner after a restart (up to one period). StartupFailureThreshold stays
    /// counted in 10-second steps: the startup budget does not change with the period.</summary>
    [Range(1, 10)]
    public int StartupPeriodSeconds { get; set; } = 10;

    /// <summary>Readiness probe period, and its initial delay, in seconds. Default 10. A
    /// shorter one puts the pod back in its Services sooner after a restart, for one sqlcmd login per
    /// period. ReadinessFailureThreshold stays counted in 10-second steps: a slow instance leaves its
    /// Service after the same time.</summary>
    [Range(1, 10)]
    public int ReadinessPeriodSeconds { get; set; } = 10;

    /// <summary>A threshold counted in 10-second steps, as a number of probes of <paramref name="periodSeconds"/>.</summary>
    internal static int InTenSecondSteps(int threshold, int periodSeconds)
        => (int)Math.Ceiling(threshold * 10.0 / Math.Clamp(periodSeconds, 1, 10));

    /// <summary>Liveness failures (30s period) before the container is restarted. Default 5.</summary>
    [Range(1, 60)]
    public int LivenessFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Whether readiness requires every user database to be out of crash recovery.
    /// Default true: <c>SELECT 1</c> on master answers minutes before the databases are usable, so the
    /// pod used to re-enter the Service while the application got errors 922/945 on every query.
    ///
    /// This setting is the escape hatch. The check is all-or-nothing: ONE database stuck in
    /// RECOVERY_PENDING for good (a lost file, a missing filegroup) keeps the pod NotReady forever,
    /// which removes it from the headless Service, makes it unreachable to the operator's own health
    /// probes, and hands a single broken database the power to look like a dead replica. Set this to
    /// false to serve the other fifty databases while the broken one is repaired.
    /// </summary>
    public bool RequireDatabasesRecovered { get; set; } = true;
}
