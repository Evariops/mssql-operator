using System.Reflection;

namespace MsSqlOperator.Operator.Registration;

/// <summary>Configuration for the home-grown operator runtime, replacing KubeOps' operator settings.</summary>
public sealed class OperatorOptions
{
    /// <summary>
    /// What this process runs. <c>All</c> — the operator, plus the breakout controller when
    /// FailoverBreakout:Enabled; <c>Operator</c> — the operator only (the breakout runs in its own
    /// Deployment); <c>Breakout</c> — the breakout controller only, under its own ServiceAccount, so the
    /// identity that taints nodes and detaches volumes no longer serves the webhook and the aggregated
    /// API. Read from the configuration before the services are registered.
    /// </summary>
    public string Role { get; set; } = "All";

    /// <summary>When true, only the elected leader runs the reconciliation watchers (Lease-based).</summary>
    public bool LeaderElection { get; set; } = true;

    /// <summary>Name of the coordination.k8s.io Lease used for leader election.</summary>
    public string LeaseName { get; set; } = "mssql-operator-leader";

    /// <summary>Namespace the operator runs in (for the Lease). Resolved from the service account if empty.</summary>
    public string? Namespace { get; set; }

    /// <summary>Unique identity of this replica in the leader election (defaults to the hostname).</summary>
    public string Identity { get; set; } = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

    /// <summary>
    /// Number of concurrent reconcile workers per entity type.
    ///
    /// Two was hard-coded and unreachable from a deployment. The detection of a dead primary is a
    /// polled reconcile, so the health cadence of the whole fleet is bounded by these workers: a
    /// couple of availability groups with an unreachable replica were enough to push every other AG's
    /// health pass from 10 seconds to minutes — the RTO of a failover grows with the size of the
    /// fleet, exactly when it must not. Bind it from the Helm chart (Operator__WorkerCount).
    /// </summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>
    /// Upper bound on a single reconcile pass, per kind. Nothing bounded it: a full
    /// backup with CommandTimeout=0, or a connection retried for minutes against a dead node, held a
    /// worker hostage indefinitely. Default 10 minutes; SqlAvailabilityGroup is tightened below since
    /// its pass drives failover detection.
    /// </summary>
    public TimeSpan DefaultReconcileTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Per-kind override of <see cref="DefaultReconcileTimeout"/> (key = CRD Kind).</summary>
    public Dictionary<string, TimeSpan> ReconcileTimeoutPerKind { get; } = new()
    {
        // The AG pass drives health checking and failover: it must never be the thing that is stuck.
        ["SqlAvailabilityGroup"] = TimeSpan.FromMinutes(2),

        // This pass runs the initial COPY_ONLY full backup that establishes the log
        // chain before ADD DATABASE (CommandTimeout=0 on purpose). Under the 10-minute
        // default, a database whose backup takes longer could NEVER join an AG: the pass was
        // cancelled and the non-resumable backup restarted from zero on every retry, forever. The
        // kind has its own workers, so a long pass starves no AG health check.
        ["SqlAvailabilityGroupDatabase"] = TimeSpan.FromHours(4),
    };

    public TimeSpan ReconcileTimeoutFor(string kind)
        => ReconcileTimeoutPerKind.TryGetValue(kind, out var v) ? v : DefaultReconcileTimeout;

    /// <summary>
    /// Periodic resync applied by the runtime whenever a reconcile succeeds without asking for
    /// a requeue: every terminal state stays convergent — manual drift (e.g. a deleted
    /// StatefulSet) is repaired within this interval without touching the CR. ±20% jitter is
    /// applied to avoid synchronized waves after a restart. Set to null to disable (tests).
    /// </summary>
    public TimeSpan? DefaultResync { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Per-kind override of <see cref="DefaultResync"/> (key = CRD Kind).</summary>
    public Dictionary<string, TimeSpan> ResyncPerKind { get; } = [];

    public TimeSpan? ResyncFor(string kind)
        => ResyncPerKind.TryGetValue(kind, out var v) ? v : DefaultResync;

    /// <summary>
    /// How many operator-managed StatefulSets may be rolling out (or not fully ready) at once
    /// before a further pod-template change waits. An operator upgrade that changes the pod template,
    /// or an edit of a SqlConfiguration shared by many instances, used to restart the whole fleet
    /// at the same time. Helm: operator.maxConcurrentRestarts (env Operator__MaxConcurrentRestarts).
    /// </summary>
    public int MaxConcurrentRestarts { get; set; } = 1;

    /// <summary>
    /// <c>Approval</c> (default) holds an in-place cumulative-update upgrade that only comes
    /// from a moved operator default until the resource carries the approval annotation — the upgrade
    /// is irreversible. <c>Automatic</c> restores the previous behaviour. A build the spec pins is
    /// always applied.
    /// </summary>
    public string EngineUpgradePolicy { get; set; } = "Approval";

    /// <summary>
    /// Registries <c>spec.image</c> may pull from (prefix match on the image reference). An
    /// empty list allows any registry. Helm: operator.allowedImageRegistries.
    /// </summary>
    public List<string> AllowedImageRegistries { get; set; } = ["mcr.microsoft.com/"];

    /// <summary>30 s / 20 s / 2 s, so that a leader which lost the Lease has exited
    /// (<see cref="LostLeadershipExitDeadline"/>) before anyone can take it — up to 15 s more to
    /// elect a new leader, the price of never having two. Checked at startup (<see cref="LeaseTiming"/>).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ceiling of the safety levels (Operator:SafetyLevelMax, Helm operator.safetyLevelMax). Each AG runs at the level
    /// of its <c>mssql-operator.evariops.com/safety-level</c> annotation (0 when absent), capped here;
    /// the next level runs in Shadow — computed and logged, not applied. 1 by default: the control
    /// registry is written in Shadow next to the status.
    /// </summary>
    public int SafetyLevelMax { get; set; } = 1;

    /// <summary>
    /// Node maintenance awareness (Operator:NodeMaintenance, Helm
    /// operator.nodeMaintenance.mode). Shadow by default: the signals of a coming stop (eviction,
    /// cordon, taints, node shutdown) are published in status.maintenance, the NodeMaintenance
    /// condition and events, and nothing is acted on. Each resource can lower it with the
    /// mssql-operator.evariops.com/node-maintenance annotation.
    /// </summary>
    public MsSqlOperator.Core.Safety.SwitchMode NodeMaintenance { get; set; } = MsSqlOperator.Core.Safety.SwitchMode.Shadow;

    /// <summary>Taint keys that announce a node removal, on top of the built-in ones
    /// (Karpenter, cluster-autoscaler, out-of-service). Helm operator.nodeMaintenance.disruptionTaints.</summary>
    public List<string> NodeMaintenanceTaints { get; set; } = [];

    public TimeSpan RenewDeadline { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan RetryPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Hard deadline for graceful shutdown after a leadership loss.</summary>
    public static readonly TimeSpan LostLeadershipExitDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Label selector of the operator's own pods (<c>k=v,k=v</c>), set by the chart. The
    /// network policies let only them reach port 1433 from the operator namespace — not every pod
    /// of that namespace. Empty: the whole namespace, as before.
    /// </summary>
    public string OperatorPodSelector { get; set; } = string.Empty;

    /// <summary>The labels of <see cref="OperatorPodSelector"/>; empty when unset or malformed.</summary>
    public IReadOnlyDictionary<string, string> OperatorPodLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in OperatorPodSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            // A malformed selector would let nobody in: fall back to the namespace.
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);
            labels[parts[0]] = parts[1];
        }
        return labels;
    }

    /// <summary>Refuses lease timings under which two leaders could overlap.</summary>
    public void EnsureSafeLeaseTiming()
        => LeaseTiming.EnsureSafe(LeaseName, LeaseDuration, RenewDeadline, RetryPeriod, LostLeadershipExitDeadline);

    /// <summary>Assemblies scanned for controllers/finalizers/validators. Defaults to the entry assembly.</summary>
    public IReadOnlyList<Assembly> Assemblies { get; set; } = [Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()];

    private const string ServiceAccountNamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    public string ResolveNamespace()
    {
        if (!string.IsNullOrEmpty(Namespace))
            return Namespace;
        var env = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        if (!string.IsNullOrEmpty(env))
            return env;
        if (File.Exists(ServiceAccountNamespacePath))
            return File.ReadAllText(ServiceAccountNamespacePath).Trim();
        return "default";
    }
}
