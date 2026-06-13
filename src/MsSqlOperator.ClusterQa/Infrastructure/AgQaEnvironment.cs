using System.Text.RegularExpressions;

namespace MsSqlOperator.ClusterQa.Infrastructure;

/// <summary>
/// Brings up the full HA fixture on the target cluster once per scenario collection: namespace,
/// secrets, a 2-replica synchronous Always On AG (co-located via amd64 nodeSelector, no anti-
/// affinity), a real seeded database joined to the AG, and the in-cluster continuous-writer probe.
/// Scenarios then exercise failovers against it. Torn down on dispose unless <c>QA_KEEP=1</c>.
///
/// Assets (manifests + seed SQL) live in <c>assets/</c> and are the single source of truth shared
/// with the manual runbook; the namespace token is substituted so the suite can run in any
/// namespace.
/// </summary>
public sealed class AgQaEnvironment : IAsyncLifetime
{
    private const string AssetNamespaceToken = "mssql-ha";
    public const string SaPassword = "Str0ng#HA#Sa#2026";
    public const string AgName = "ha";
    public const string Database = "ShopDB";

    public Kubectl Kubectl { get; }
    public string Namespace { get; }

    private readonly string _assetsDir = Path.Combine(AppContext.BaseDirectory, "assets");

    public AgQaEnvironment()
    {
        Namespace = QaConfig.Namespace;
        Kubectl = new Kubectl(QaConfig.KubeContext, Namespace);
    }

    public async ValueTask InitializeAsync()
    {
        if (!QaConfig.Enabled) return; // collection fixtures run even when all facts skip; do nothing.

        await Kubectl.ApplyAsync(Asset("ag.yaml"));
        await WaitForAgPhaseAsync("Running", TimeSpan.FromMinutes(8));

        // Seed real data on the current primary, then join the DB to the AG and wait for sync.
        var primary = await PrimaryAsync();
        await Kubectl.ExecSqlScriptAsync(primary, SaPassword, Asset("seed.sql"));
        await Kubectl.ApplyAsync(Asset("agdb.yaml"));
        await WaitForSagdbAsync("Synchronized", TimeSpan.FromMinutes(5));

        await Kubectl.ApplyAsync(Asset("probe.yaml"));
        await Kubectl.RunAsync(default, "-n", Namespace, "rollout", "status", "deploy/ha-probe", "--timeout=120s");
    }

    public async ValueTask DisposeAsync()
    {
        if (!QaConfig.Enabled || QaConfig.Keep) return;
        await Kubectl.DeleteNamespaceAsync(Namespace);
    }

    /// <summary>The replica the operator currently reports as PRIMARY (status.primaryReplica).</summary>
    public Task<string> PrimaryAsync(CancellationToken ct = default)
        => Kubectl.GetJsonPathAsync("sqlavailabilitygroup", AgName, "{.status.primaryReplica}", ct);

    /// <summary>The pod the R/W listener Endpoints currently route to (the confirmed writer).</summary>
    public Task<string> ListenerTargetAsync(CancellationToken ct = default)
        => Kubectl.GetJsonPathAsync("endpoints", $"{AgName}-listener", "{.subsets[0].addresses[0].targetRef.name}", ct);

    /// <summary>Runs a scalar query against a pod and returns the trimmed single value.</summary>
    public async Task<string> ScalarAsync(string pod, string query, string? database = null, CancellationToken ct = default)
        => (await Kubectl.ExecSqlAsync(pod, SaPassword, $"SET NOCOUNT ON; {query}", database ?? Database, ct)).Trim();

    public Task<string> ProbeLogsAsync(int tail = 4000, CancellationToken ct = default)
        => Kubectl.LogsAsync("app=ha-probe", tail, bySelector: true, ct);

    public Task FailoverToAsync(int ordinal, CancellationToken ct = default)
        => Kubectl.AnnotateAsync("sqlavailabilitygroup", AgName,
            $"mssql-operator.evariops.com/failover-to={ordinal}", ct);

    public async Task WaitForAgPhaseAsync(string phase, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Kubectl.GetJsonPathAsync("sqlavailabilitygroup", AgName, "{.status.phase}", ct) == phase) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"AG {AgName} did not reach phase {phase} within {timeout}");
    }

    private async Task WaitForSagdbAsync(string phase, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Kubectl.GetJsonPathAsync("sqlavailabilitygroupdatabase", "shopdb-membership", "{.status.phase}", ct) == phase) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"Database membership did not reach {phase} within {timeout}");
    }

    private string Asset(string file)
    {
        var raw = File.ReadAllText(Path.Combine(_assetsDir, file));
        // Substitute the namespace token everywhere (manifest metadata + the probe's listener FQDN).
        raw = Regex.Replace(raw, $@"\b{Regex.Escape(AssetNamespaceToken)}\b", Namespace);
        // On a multi-node cluster, spread the replicas one-per-node so a node fault is a real test;
        // on the single-amd64-node case (e.g. a test cluster) the token expands to nothing (co-location).
        var antiAffinity = QaConfig.AntiAffinity
            ? "  antiAffinity:\n    requiredSelfAntiAffinity:\n      topologyKey: kubernetes.io/hostname"
            : "";
        return raw.Replace("# __ANTIAFFINITY__", antiAffinity);
    }
}

[CollectionDefinition(Name)]
public sealed class AgQaCollection : ICollectionFixture<AgQaEnvironment>
{
    public const string Name = "AvailabilityGroup HA (live cluster)";
}
