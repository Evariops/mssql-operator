using System.Reflection;

namespace MsSqlOperator.Operator.Registration;

/// <summary>Configuration for the home-grown operator runtime, replacing KubeOps' operator settings.</summary>
public sealed class OperatorOptions
{
    /// <summary>When true, only the elected leader runs the reconciliation watchers (Lease-based).</summary>
    public bool LeaderElection { get; set; } = true;

    /// <summary>Name of the coordination.k8s.io Lease used for leader election.</summary>
    public string LeaseName { get; set; } = "mssql-operator-leader";

    /// <summary>Namespace the operator runs in (for the Lease). Resolved from the service account if empty.</summary>
    public string? Namespace { get; set; }

    /// <summary>Unique identity of this replica in the leader election (defaults to the hostname).</summary>
    public string Identity { get; set; } = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

    /// <summary>Number of concurrent reconcile workers per entity type.</summary>
    public int WorkerCount { get; set; } = 2;

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

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RenewDeadline { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan RetryPeriod { get; set; } = TimeSpan.FromSeconds(2);

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
