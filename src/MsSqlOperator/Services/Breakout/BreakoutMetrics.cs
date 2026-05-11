using System.Diagnostics.Metrics;

namespace MsSqlOperator.Services.Breakout;

public sealed class BreakoutMetrics : IDisposable
{
    public const string MeterName = "MsSqlOperator.Breakout";

    private readonly Meter _meter;
    private readonly Counter<long> _fenceTotal;
    private readonly Counter<long> _unfenceTotal;
    private readonly Counter<long> _volumeDetachTotal;
    private readonly Counter<long> _forceDeleteTotal;
    private readonly UpDownCounter<int> _nodesFenced;
    private readonly Histogram<double> _loopDuration;

    public BreakoutMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _fenceTotal = _meter.CreateCounter<long>(
            "mssql.breakout.fence.total",
            description: "Total number of fencing operations");

        _unfenceTotal = _meter.CreateCounter<long>(
            "mssql.breakout.unfence.total",
            description: "Total number of unfencing operations");

        _volumeDetachTotal = _meter.CreateCounter<long>(
            "mssql.breakout.volume.detach.total",
            description: "Total number of VolumeAttachments deleted");

        _forceDeleteTotal = _meter.CreateCounter<long>(
            "mssql.breakout.force.delete.total",
            description: "Total number of force-deleted pods");

        _nodesFenced = _meter.CreateUpDownCounter<int>(
            "mssql.breakout.nodes.fenced",
            description: "Number of nodes currently fenced");

        _loopDuration = _meter.CreateHistogram<double>(
            "mssql.breakout.loop.duration.seconds",
            unit: "s",
            description: "Duration of the breakout control loop");
    }

    public void RecordFence(string nodeName) => _fenceTotal.Add(1, new KeyValuePair<string, object?>("node", nodeName));
    public void RecordUnfence(string nodeName) => _unfenceTotal.Add(1, new KeyValuePair<string, object?>("node", nodeName));
    public void RecordVolumeDetach(string podName) => _volumeDetachTotal.Add(1, new KeyValuePair<string, object?>("pod", podName));
    public void RecordForceDelete(string podName) => _forceDeleteTotal.Add(1, new KeyValuePair<string, object?>("pod", podName));
    public void NodeFenced() => _nodesFenced.Add(1);
    public void NodeUnfenced() => _nodesFenced.Add(-1);
    public void RecordLoopDuration(double seconds) => _loopDuration.Record(seconds);

    public void Dispose() => _meter.Dispose();
}
