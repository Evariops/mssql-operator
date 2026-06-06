using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Services.Metrics;

public sealed class OperatorMetrics : IReconcileMetrics, IDisposable
{
    public const string MeterName = "MsSqlOperator";

    private readonly Meter _meter;
    private readonly Counter<long> _reconciliationTotal;
    private readonly Counter<long> _reconciliationErrorsTotal;
    private readonly Histogram<double> _reconciliationDuration;
    private readonly UpDownCounter<int> _instancesTotal;
    private readonly Counter<long> _agFailoverTotal;
    private readonly Histogram<double> _agHealthCheckDuration;

    // Latest observed AG/replica snapshots, surfaced as Prometheus gauges via ObservableGauge.
    private readonly ConcurrentDictionary<string, AgGaugeSnapshot> _agSnapshots = new();
    private readonly ConcurrentDictionary<(string Ag, string Replica), ReplicaLagSnapshot> _lagSnapshots = new();
    private readonly ConcurrentDictionary<string, int> _rollingUpdate = new();

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
            "mssqloperator.reconciliation.duration.seconds",
            unit: "s",
            description: "Duration of reconciliation in seconds");

        _instancesTotal = _meter.CreateUpDownCounter<int>(
            "mssqloperator.instances.total",
            description: "Current number of managed SQL Server instances");

        _agFailoverTotal = _meter.CreateCounter<long>(
            "mssqloperator.ag.failover.total",
            description: "Total number of AG failovers");

        _agHealthCheckDuration = _meter.CreateHistogram<double>(
            "mssqloperator.ag.health_check.duration.seconds",
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
    }

    private static KeyValuePair<string, object?>[] Ag(string ag) => [new("ag", ag)];
    private static KeyValuePair<string, object?>[] AgReplica((string Ag, string Replica) k) =>
        [new("ag", k.Ag), new("replica", k.Replica)];

    public void RecordReconciliation(string controller, double durationSeconds, bool success)
    {
        var tags = new TagList { { "controller", controller } };
        _reconciliationTotal.Add(1, tags);
        _reconciliationDuration.Record(durationSeconds, tags);
        if (!success) _reconciliationErrorsTotal.Add(1, tags);
    }

    public void InstanceCreated(string mode, string version) =>
        _instancesTotal.Add(1, new TagList { { "mode", mode }, { "version", version } });

    public void InstanceDeleted(string mode, string version) =>
        _instancesTotal.Add(-1, new TagList { { "mode", mode }, { "version", version } });

    public void AgFailover(string agName) =>
        _agFailoverTotal.Add(1, new TagList { { "ag", agName } });

    public void RecordAgHealthCheckDuration(string agName, double seconds) =>
        _agHealthCheckDuration.Record(seconds, new TagList { { "ag", agName } });

    /// <summary>Publish the latest replica counts for the AG.</summary>
    public void SetAgReplicaGauges(string agName, int total, int synchronized, int disconnected) =>
        _agSnapshots[agName] = new AgGaugeSnapshot(total, synchronized, disconnected);

    /// <summary>Publish the latest replication-lag values for a replica.</summary>
    public void SetReplicaLag(string agName, string replica, long logSendQueueBytes, long redoQueueBytes) =>
        _lagSnapshots[(agName, replica)] = new ReplicaLagSnapshot(logSendQueueBytes, redoQueueBytes);

    public void SetRollingUpdateInProgress(string agName, bool inProgress) =>
        _rollingUpdate[agName] = inProgress ? 1 : 0;

    /// <summary>Drop an AG's gauges (e.g. on deletion) so it stops being scraped.</summary>
    public void ForgetAg(string agName)
    {
        _agSnapshots.TryRemove(agName, out _);
        _rollingUpdate.TryRemove(agName, out _);
        foreach (var key in _lagSnapshots.Keys.Where(k => k.Ag == agName).ToList())
            _lagSnapshots.TryRemove(key, out _);
    }

    public void Dispose() => _meter.Dispose();

    private readonly record struct AgGaugeSnapshot(int Total, int Synchronized, int Disconnected);
    private readonly record struct ReplicaLagSnapshot(long LogSendQueueBytes, long RedoQueueBytes);
}
