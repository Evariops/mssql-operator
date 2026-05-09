using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MsSqlOperator.Services.Metrics;

public sealed class OperatorMetrics : IDisposable
{
    public const string MeterName = "MsSqlOperator";

    private readonly Meter _meter;
    private readonly Counter<long> _reconciliationTotal;
    private readonly Counter<long> _reconciliationErrorsTotal;
    private readonly Histogram<double> _reconciliationDuration;
    private readonly UpDownCounter<int> _instancesTotal;
    private readonly Counter<long> _agFailoverTotal;

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
    }

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

    public void Dispose() => _meter.Dispose();
}
