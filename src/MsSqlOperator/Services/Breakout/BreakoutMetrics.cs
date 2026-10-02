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
    private readonly Histogram<double> _loopDuration;

    // The number of fenced nodes is read from the blacklist (the source of truth) on every
    // pass. An in-memory up/down counter restarted at zero with every process, and went negative
    // when nodes fenced by a previous process were unfenced.
    private int? _fencedNodes;

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

        _meter.CreateObservableGauge(
            "mssql.breakout.nodes.fenced",
            () => _fencedNodes is { } count ? [new Measurement<int>(count)] : Array.Empty<Measurement<int>>(),
            description: "Number of nodes currently fenced (the blacklist, as of the last control-loop pass)");

        _loopDuration = _meter.CreateHistogram<double>(
            "mssql.breakout.loop.duration.seconds",
            unit: "s",
            description: "Duration of the breakout control loop");
    }

    public void RecordFence(string nodeName) => _fenceTotal.Add(1, new KeyValuePair<string, object?>("node", nodeName));
    public void RecordUnfence(string nodeName) => _unfenceTotal.Add(1, new KeyValuePair<string, object?>("node", nodeName));
    public void RecordVolumeDetach(string podName) => _volumeDetachTotal.Add(1, new KeyValuePair<string, object?>("pod", podName));
    public void RecordForceDelete(string podName) => _forceDeleteTotal.Add(1, new KeyValuePair<string, object?>("pod", podName));
    /// <summary>The number of blacklisted nodes, as of the last control-loop pass.</summary>
    public void SetFencedNodes(int count) => _fencedNodes = count;
    public void RecordLoopDuration(double seconds) => _loopDuration.Record(seconds);

    public void Dispose() => _meter.Dispose();
}
