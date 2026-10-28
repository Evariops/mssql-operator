using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// The failover intents of the status go to the control registry
/// too. At safety level 1 in Shadow — the default, below the ceiling — the registry is written next to
/// the status and every gap between the two is logged, nothing depends on it. In Enforce (an AG at
/// level 1 or above), a registry write that fails stops the operation: no effect without its intent.
/// </summary>
public sealed class ControlMirror(ControlRegistry registry, Operator.Registration.OperatorOptions options, ILogger<ControlMirror> logger)
{
    /// <summary>Level 1 for this AG: Enforce, Shadow, or not running.</summary>
    public SafetyMode? Mode(SqlAvailabilityGroup ag)
        => SafetyLevel.Mode(1, Level(ag), options.SafetyLevelMax);

    public int Level(SqlAvailabilityGroup ag)
        => SafetyLevel.Effective(ag.Metadata.Annotations?.TryGetValue(SafetyLevel.Annotation, out var level) == true ? level : null,
            options.SafetyLevelMax);

    /// <summary>The intent id of a status failover record.</summary>
    public static string IdOf(AgLastFailoverStatus failover) => $"fo-{failover.Timestamp:yyyyMMddHHmmss}-{failover.To}";

    public Task BeginAsync(SqlAvailabilityGroup ag, AgLastFailoverStatus failover, CancellationToken ct)
        => RunAsync(ag, "begin", async record =>
        {
            await registry.BeginAsync(record, new ControlIntent
            {
                Id = IdOf(failover),
                Type = failover.DataLoss ? "ForceFailoverDataLoss" : "Failover",
                From = failover.From,
                To = failover.To,
                Stage = "Promoting",
                StartedAt = failover.Timestamp,
            }, ct);
        }, ct);

    /// <summary>The failover took effect: the intent is closed, its divergence recorded.</summary>
    public Task CompleteAsync(SqlAvailabilityGroup ag, AgLastFailoverStatus failover, CancellationToken ct)
        => RunAsync(ag, "complete", async record =>
        {
            await registry.CompleteAsync(record, IdOf(failover), spec =>
            {
                if (!failover.DataLoss)
                    return;
                var divergence = AgDivergences.Of(failover);
                spec.OpenDivergences ??= [];
                if (spec.OpenDivergences.All(d => d.Id != divergence.Id))
                    spec.OpenDivergences.Add(new ControlDivergence { Id = divergence.Id, From = failover.From, To = failover.To, At = failover.Timestamp, Epoch = spec.Epoch });
            }, ct);
        }, ct);

    public Task AbortAsync(SqlAvailabilityGroup ag, AgLastFailoverStatus failover, CancellationToken ct)
        => RunAsync(ag, "abort", record => registry.AbortAsync(record, IdOf(failover), ct), ct);

    private async Task RunAsync(SqlAvailabilityGroup ag, string what, Func<SqlControlRecord, Task> write, CancellationToken ct)
    {
        var mode = Mode(ag);
        if (mode is null)
            return;
        try
        {
            var record = await registry.EnsureAsync(ag, SqlControlRecord.ForAg(ag.Metadata.Name), spec => Reconstruct(ag, spec), ct);
            await write(record);
            Compare(ag, await registry.GetAsync(record.Metadata.Name, record.Metadata.NamespaceProperty, ct));
        }
        catch (Exception ex) when (mode == SafetyMode.Shadow && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Control registry ({What}, Shadow) of AG {Ag} failed; the status remains the reference", what, ag.Metadata.Name);
        }
    }

    /// <summary>The record of an AG that has none is rebuilt from what is known of it.</summary>
    internal static void Reconstruct(SqlAvailabilityGroup ag, SqlControlRecordSpec spec)
    {
        spec.OpenDivergences = AgDivergences.Open(ag)
            .Select(d => new ControlDivergence { Id = d.Id, From = d.From, To = d.To, At = d.At })
            .ToList();
        if (ag.Status?.MachineKeySha256 is { } sha)
            spec.Keys = new ControlKeys { MachineKeySha256 = sha };
    }

    /// <summary>Logs every gap between the status and the registry.</summary>
    private void Compare(SqlAvailabilityGroup ag, SqlControlRecord? record)
    {
        var statusIntent = ag.Status?.FailoverIntent is { } i ? IdOf(i) : null;
        var registryIntent = record?.Spec.Intent?.Id;
        if (statusIntent != registryIntent)
            logger.LogInformation("Control registry gap on AG {Ag}: status intent {Status}, registry intent {Registry}",
                ag.Metadata.Name, statusIntent ?? "(none)", registryIntent ?? "(none)");
        var statusDivergences = AgDivergences.Open(ag).Select(d => d.Id).Order(StringComparer.Ordinal).ToList();
        var registryDivergences = (record?.Spec.OpenDivergences ?? []).Select(d => d.Id).Order(StringComparer.Ordinal).ToList();
        if (!statusDivergences.SequenceEqual(registryDivergences))
            logger.LogInformation("Control registry gap on AG {Ag}: status divergences [{Status}], registry divergences [{Registry}]",
                ag.Metadata.Name, string.Join(",", statusDivergences), string.Join(",", registryDivergences));
        if (record?.Spec.Keys?.MachineKeySha256 is { } recorded && ag.Status?.MachineKeySha256 is { } current && recorded != current)
            logger.LogWarning("Control registry gap on AG {Ag}: machine key {Registry} in the registry, {Status} in the status",
                ag.Metadata.Name, recorded[..12], current[..12]);
        if (record?.Spec.ReconstructedAt is { } rebuilt)
            logger.LogInformation("Control record of AG {Ag} was reconstructed at {At:O} (observation mode)", ag.Metadata.Name, rebuilt);
    }
}
