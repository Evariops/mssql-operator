using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Services.Metrics;

public sealed class OperatorMetrics : IReconcileMetrics, IDisposable
{
    public const string MeterName = "MsSqlOperator";

    private readonly Meter _meter;

    /// <summary>The meter of this instance, for tests that listen to one instance only.</summary>
    internal Meter Meter => _meter;
    private readonly Counter<long> _reconciliationTotal;
    private readonly Counter<long> _reconciliationErrorsTotal;
    private readonly Histogram<double> _reconciliationDuration;
    private readonly Counter<long> _agFailoverTotal;
    private readonly Histogram<double> _agHealthCheckDuration;
    private readonly Counter<long> _ownedWatchEnqueuesTotal;
    private readonly Counter<long> _operatorLogBackupsTotal;
    private readonly Counter<long> _writesDroppedTotal;
    private readonly Counter<long> _actuationDecisionsTotal;

    // Supplies the per-child-kind age (seconds since last watch activity) for the
    // owned-watch zombie gauge. Set by OwnedResourceWatcher when it starts.
    private volatile Func<IEnumerable<Measurement<double>>>? _ownedWatchAgeProvider;

    // Latest observed AG/replica snapshots, surfaced as Prometheus gauges via ObservableGauge.
    // Keyed (and tagged) by namespace too — two AGs of the same name in two namespaces used
    // to overwrite each other's gauges, and deleting one erased both.
    private readonly ConcurrentDictionary<(string Namespace, string Ag), AgGaugeSnapshot> _agSnapshots = new();
    private readonly ConcurrentDictionary<(string Namespace, string Ag, string Replica), ReplicaLagSnapshot> _lagSnapshots = new();
    private readonly ConcurrentDictionary<(string Namespace, string Ag), int> _rollingUpdate = new();

    // Per-resource roll-ups that used to live as fan-out arrays in CRD status. Keyed by
    // (kind, namespace, name); cleared via ForgetResource on delete.
    private readonly ConcurrentDictionary<string, Func<(int Depth, double OldestAgeSeconds)>> _queues = new();
    private readonly ConcurrentDictionary<ResourceKey, TargetRollup> _targets = new();
    private readonly ConcurrentDictionary<ResourceKey, TdeDbRollup> _tdeDatabases = new();
    private readonly ConcurrentDictionary<ResourceKey, ProcRollup> _procedures = new();
    private readonly ConcurrentDictionary<ResourceKey, int> _needsAttention = new();

    public OperatorMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _reconciliationTotal = _meter.CreateCounter<long>(
            "mssqloperator.reconciliation.total",
            description: "Total number of reconciliations");

        _reconciliationErrorsTotal = _meter.CreateCounter<long>(
            "mssqloperator.reconciliation.errors.total",
            description: "Total number of reconciliation errors");

        _reconciliationDuration = _meter.CreateHistogram<double>(
            "mssqloperator.reconciliation.duration",
            unit: "s",
            description: "Duration of reconciliation in seconds");

        // An in-memory UpDownCounter is wrong after every operator restart; the
        // gauge is computed from the tracked-instance snapshot, and named .count (gauge
        // semantics) instead of .total (counter semantics).
        _meter.CreateObservableGauge("mssqloperator.instances.count",
            () => _instances
                .GroupBy(kv => (kv.Value.Mode, kv.Value.Version))
                .Select(g => new Measurement<int>(g.Count(),
                    new TagList { { "mode", g.Key.Mode }, { "version", g.Key.Version } })),
            description: "Current number of managed SQL Server instances");

        _agFailoverTotal = _meter.CreateCounter<long>(
            "mssqloperator.ag.failover.total",
            description: "Total number of AG failovers");

        _agHealthCheckDuration = _meter.CreateHistogram<double>(
            "mssqloperator.ag.health_check.duration",
            unit: "s",
            description: "Duration of an AG health check in seconds");

        // ── Observable gauges ───────────────────────────────
        _meter.CreateObservableGauge("mssqloperator.ag.replicas.total",
            () => _agSnapshots.Select(kv => new Measurement<int>(kv.Value.Total, Ag(kv.Key))),
            description: "Total replicas in the AG");
        _meter.CreateObservableGauge("mssqloperator.ag.replicas.synchronized",
            () => _agSnapshots.Select(kv => new Measurement<int>(kv.Value.Synchronized, Ag(kv.Key))),
            description: "Synchronized replicas in the AG");
        _meter.CreateObservableGauge("mssqloperator.ag.replicas.disconnected",
            () => _agSnapshots.Select(kv => new Measurement<int>(kv.Value.Disconnected, Ag(kv.Key))),
            description: "Disconnected replicas in the AG");
        _meter.CreateObservableGauge("mssqloperator.ag.log_send_queue.bytes",
            () => _lagSnapshots.Select(kv => new Measurement<long>(kv.Value.LogSendQueueBytes, AgReplica(kv.Key))),
            unit: "By", description: "Log send queue size per replica");
        _meter.CreateObservableGauge("mssqloperator.ag.redo_queue.bytes",
            () => _lagSnapshots.Select(kv => new Measurement<long>(kv.Value.RedoQueueBytes, AgReplica(kv.Key))),
            unit: "By", description: "Redo queue size per replica");
        _meter.CreateObservableGauge("mssqloperator.ag.rolling_update.in_progress",
            () => _rollingUpdate.Select(kv => new Measurement<int>(kv.Value, Ag(kv.Key))),
            description: "1 while an AG-aware rolling update is in progress, else 0");

        // ── Work-queue saturation ─────────────────────────────
        // An operator whose queue is backed up stops health-checking its availability groups, and
        // nothing said so: /readyz only proves the API server is reachable. Alert on
        // workqueue_oldest_age_seconds > 3 × failover.healthCheckIntervalSeconds.
        _meter.CreateObservableGauge("mssqloperator.workqueue.depth",
            () => _queues.Select(kv => new Measurement<int>(kv.Value().Depth, new KeyValuePair<string, object?>("kind", kv.Key))),
            description: "Resources waiting to be reconciled, per kind");
        _meter.CreateObservableGauge("mssqloperator.workqueue.oldest_age",
            () => _queues.Select(kv => new Measurement<double>(kv.Value().OldestAgeSeconds, new KeyValuePair<string, object?>("kind", kv.Key))),
            unit: "s",
            description: "How long the oldest waiting resource has been waiting, per kind");

        // ── Per-resource roll-ups ────────────────────────────
        _meter.CreateObservableGauge("mssqloperator.targets.total",
            () => _targets.Select(kv => new Measurement<int>(kv.Value.Total, Res(kv.Key))),
            description: "Targets matched by a multi-target resource");
        _meter.CreateObservableGauge("mssqloperator.targets.ready",
            () => _targets.Select(kv => new Measurement<int>(kv.Value.Ready, Res(kv.Key))),
            description: "Targets successfully provisioned");
        _meter.CreateObservableGauge("mssqloperator.targets.failed",
            () => _targets.Select(kv => new Measurement<int>(kv.Value.Failed, Res(kv.Key))),
            description: "Targets that failed provisioning");

        _meter.CreateObservableGauge("mssqloperator.tde.databases.encrypted",
            () => _tdeDatabases.Select(kv => new Measurement<int>(kv.Value.Encrypted, Res(kv.Key))),
            description: "Databases encrypted by a SqlTdeCertificate");
        _meter.CreateObservableGauge("mssqloperator.tde.databases.pending",
            () => _tdeDatabases.Select(kv => new Measurement<int>(kv.Value.Pending, Res(kv.Key))),
            description: "Databases pending encryption");
        _meter.CreateObservableGauge("mssqloperator.tde.databases.failed",
            () => _tdeDatabases.Select(kv => new Measurement<int>(kv.Value.Failed, Res(kv.Key))),
            description: "Databases that failed encryption");

        _meter.CreateObservableGauge("mssqloperator.storedproc.deployed",
            () => _procedures.Select(kv => new Measurement<int>(kv.Value.Deployed, Res(kv.Key))),
            description: "Stored procedures actually deployed");
        _meter.CreateObservableGauge("mssqloperator.storedproc.desired",
            () => _procedures.Select(kv => new Measurement<int>(kv.Value.Desired, Res(kv.Key))),
            description: "Stored procedures defined in the spec");

        // The NeedsAttention condition, as the one gauge an on-call alerts on.
        _meter.CreateObservableGauge("mssqloperator.needs_attention",
            () => _needsAttention.Select(kv => new Measurement<int>(kv.Value, Res(kv.Key))),
            description: "1 when a human decision is required on the resource (NeedsAttention condition), else 0");

        // Owned-resource watch observability.
        _ownedWatchEnqueuesTotal = _meter.CreateCounter<long>(
            "mssqloperator.owned_watch.enqueues.total",
            description: "Parent reconciles enqueued from an owned-child watch event");

        _actuationDecisionsTotal = _meter.CreateCounter<long>(
            "mssqloperator.actuation.decisions.total",
            description: "Decisions of the actuation gateway, by risk class and decision");

        _writesDroppedTotal = _meter.CreateCounter<long>(
            "mssqloperator.writes.dropped.total",
            description: "Writes to the API server dropped under pressure, by class (events first)");

        _operatorLogBackupsTotal = _meter.CreateCounter<long>(
            "mssqloperator.operator_log_backups.total",
            description: "Log backups the operator took because nothing else truncated the log");
        _meter.CreateObservableGauge("mssqloperator.owned_watch.last_event_age_seconds",
            () => _ownedWatchAgeProvider?.Invoke() ?? [],
            unit: "s",
            description: "Seconds since the last event on each owned-child watch (zombie detection)");
    }

    private static KeyValuePair<string, object?>[] Ag((string Namespace, string Ag) k) => [new("namespace", k.Namespace), new("ag", k.Ag)];
    private static KeyValuePair<string, object?>[] AgReplica((string Namespace, string Ag, string Replica) k) =>
        [new("namespace", k.Namespace), new("ag", k.Ag), new("replica", k.Replica)];
    private static KeyValuePair<string, object?>[] Res(ResourceKey k) =>
        [new("kind", k.Kind), new("namespace", k.Namespace), new("name", k.Name)];

    public void RecordReconciliation(string controller, double durationSeconds, bool success)
    {
        var tags = new TagList { { "controller", controller } };
        _reconciliationTotal.Add(1, tags);
        _reconciliationDuration.Record(durationSeconds, tags);
        if (!success) _reconciliationErrorsTotal.Add(1, tags);
    }

    /// <summary>Work-queue saturation per kind, read at collection time.</summary>
    public void RegisterQueue(string kind, Func<(int Depth, double OldestAgeSeconds)> probe)
        => _queues[kind] = probe;

    private readonly ConcurrentDictionary<ResourceKey, (string Mode, string Version)> _instances = new();

    /// <summary>Records that an instance exists — called on every reconcile, idempotent.</summary>
    public void TrackInstance(string ns, string name, string mode, string version) =>
        _instances[new ResourceKey("SqlInstance", ns, name)] = (mode, version);

    /// <summary>A decision of the actuation gateway.</summary>
    public void ActuationDecision(string risk, string decision) =>
        _actuationDecisionsTotal.Add(1, new TagList { { "risk", risk }, { "decision", decision } });

    /// <summary>A write dropped under pressure (class Event, Status or Control).</summary>
    public void WriteDropped(string writeClass, string type) =>
        _writesDroppedTotal.Add(1, new TagList { { "class", writeClass }, { "type", type } });

    /// <summary>The operator backed up the log of an AG database itself.</summary>
    public void OperatorLogBackup(string ns, string database) =>
        _operatorLogBackupsTotal.Add(1, new TagList { { "namespace", ns }, { "database", database } });

    public void AgFailover(string ns, string agName) =>
        _agFailoverTotal.Add(1, new TagList { { "namespace", ns }, { "ag", agName } });

    /// <summary>A child-resource watch event routed a reconcile to its parent runtime.</summary>
    public void RecordOwnedWatchEnqueue(string childKind, string parentKind) =>
        _ownedWatchEnqueuesTotal.Add(1, new TagList { { "child_kind", childKind }, { "parent_kind", parentKind } });

    /// <summary>Register the source of per-child-kind watch ages for the zombie gauge.</summary>
    public void SetOwnedWatchAgeProvider(Func<IEnumerable<Measurement<double>>> provider) =>
        _ownedWatchAgeProvider = provider;

    public void RecordAgHealthCheckDuration(string ns, string agName, double seconds) =>
        _agHealthCheckDuration.Record(seconds, new TagList { { "namespace", ns }, { "ag", agName } });

    /// <summary>Publish the latest replica counts for the AG.</summary>
    public void SetAgReplicaGauges(string ns, string agName, int total, int synchronized, int disconnected) =>
        _agSnapshots[(ns, agName)] = new AgGaugeSnapshot(total, synchronized, disconnected);

    /// <summary>Publish the latest replication-lag values for a replica.</summary>
    public void SetReplicaLag(string ns, string agName, string replica, long logSendQueueBytes, long redoQueueBytes) =>
        _lagSnapshots[(ns, agName, replica)] = new ReplicaLagSnapshot(logSendQueueBytes, redoQueueBytes);

    public void SetRollingUpdateInProgress(string ns, string agName, bool inProgress) =>
        _rollingUpdate[(ns, agName)] = inProgress ? 1 : 0;

    /// <summary>Drop an AG's gauges (e.g. on deletion) so it stops being scraped.</summary>
    public void ForgetAg(string ns, string agName)
    {
        _agSnapshots.TryRemove((ns, agName), out _);
        _rollingUpdate.TryRemove((ns, agName), out _);
        foreach (var key in _lagSnapshots.Keys.Where(k => k.Namespace == ns && k.Ag == agName).ToList())
            _lagSnapshots.TryRemove(key, out _);
    }

    /// <summary>Publish target roll-up counts for a multi-target resource (SqlUser/StoredProc/AgentJob/...).</summary>
    public void SetTargets(string kind, string ns, string name, int ready, int total, int failed) =>
        _targets[new ResourceKey(kind, ns, name)] = new TargetRollup(ready, total, failed);

    /// <summary>Publish TDE database roll-up counts for a SqlTdeCertificate.</summary>
    public void SetTdeDatabases(string ns, string name, int encrypted, int pending, int failed) =>
        _tdeDatabases[new ResourceKey("SqlTdeCertificate", ns, name)] = new TdeDbRollup(encrypted, pending, failed);

    /// <summary>Publish stored-procedure deployment roll-up counts for a SqlStoredProcedure.</summary>
    public void SetStoredProcedures(string ns, string name, int deployed, int desired) =>
        _procedures[new ResourceKey("SqlStoredProcedure", ns, name)] = new ProcRollup(deployed, desired);

    /// <summary>Publish whether a human decision is required on a resource.</summary>
    public void SetNeedsAttention(string kind, string ns, string name, bool required) =>
        _needsAttention[new ResourceKey(kind, ns, name)] = required ? 1 : 0;

    /// <summary>Drop a resource's roll-up gauges (e.g. on deletion) so it stops being scraped.</summary>
    public void ForgetResource(string kind, string ns, string name)
    {
        var key = new ResourceKey(kind, ns, name);
        _instances.TryRemove(key, out _);
        _targets.TryRemove(key, out _);
        _tdeDatabases.TryRemove(key, out _);
        _procedures.TryRemove(key, out _);
        _needsAttention.TryRemove(key, out _);
    }

    public void Dispose() => _meter.Dispose();

    private readonly record struct AgGaugeSnapshot(int Total, int Synchronized, int Disconnected);
    private readonly record struct ReplicaLagSnapshot(long LogSendQueueBytes, long RedoQueueBytes);
    private readonly record struct ResourceKey(string Kind, string Namespace, string Name);
    private readonly record struct TargetRollup(int Ready, int Total, int Failed);
    private readonly record struct TdeDbRollup(int Encrypted, int Pending, int Failed);
    private readonly record struct ProcRollup(int Deployed, int Desired);
}
