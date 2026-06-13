namespace MsSqlOperator.ClusterQa.Infrastructure;

/// <summary>
/// Environment-driven configuration for the cluster-QA run. All knobs are env vars so the same
/// suite can target a kind cluster, a staging cluster, or production read-only — without code
/// changes. <see cref="Enabled"/> is the master gate (see <see cref="ClusterFactAttribute"/>).
/// </summary>
public static class QaConfig
{
    /// <summary>Master gate: scenarios run only when QA_CLUSTER is truthy. Default off, so an
    /// accidental `dotnet test` on a dev box skips every scenario instead of failing.</summary>
    public static bool Enabled =>
        (Environment.GetEnvironmentVariable("QA_CLUSTER") ?? "").Trim() is "1" or "true" or "yes";

    /// <summary>kubeconfig context to target. Empty => current context.</summary>
    public static string KubeContext => Environment.GetEnvironmentVariable("QA_KUBE_CONTEXT") ?? "";

    /// <summary>Namespace the operator runs in (for log scraping / RBAC checks).</summary>
    public static string OperatorNamespace => Env("QA_OPERATOR_NS", "mssql-operator");

    /// <summary>Namespace the QA workloads (AG, DB, probe) are created in. Wiped on teardown.</summary>
    public static string Namespace => Env("QA_NAMESPACE", "mssql-ha-qa");

    /// <summary>arch the SQL pods must be pinned to (SQL Server is amd64-only).</summary>
    public static string SqlArch => Env("QA_SQL_ARCH", "amd64");

    /// <summary>Spread the AG replicas one-per-node (requiredSelfAntiAffinity). Needs as many
    /// schedulable amd64 nodes as replicas; makes a node-fault test meaningful on a multi-node cluster.</summary>
    public static bool AntiAffinity => Truthy("QA_ANTIAFFINITY");

    /// <summary>Keep the namespace after the run (for post-mortem). QA_KEEP=1.</summary>
    public static bool Keep => (Environment.GetEnvironmentVariable("QA_KEEP") ?? "").Trim() is "1" or "true";

    /// <summary>Upper bound (seconds) on the write interruption a planned failover may cause before
    /// the scenario fails. A synchronous-commit AG should repoint the listener well within this.</summary>
    public static int MaxPlannedFailoverGapSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("QA_MAX_FAILOVER_GAP_S"), out var v) ? v : 30;

    private static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static bool Truthy(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? "").Trim() is "1" or "true" or "yes";
}
