using System.Globalization;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// The data-loss failovers whose divergent replicas are not settled yet. The single
/// LastFailover slot hid them: a planned failover after a forced one let the replica that held the
/// discarded commits rejoin — and lose them — without anyone being asked.
/// </summary>
public static class AgDivergences
{
    /// <summary>Bound of status.openDivergences: etcd holds bounded state only.</summary>
    public const int MaxOpen = 16;

    /// <summary>dv-&lt;yyyyMMddHHmmss&gt;-&lt;promoted replica&gt;, what the approval annotation names.</summary>
    public static string IdOf(DateTime? at, string? to)
        => $"dv-{(at is { } t ? t.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "legacy")}-{to}";

    public static AgDivergenceStatus Of(AgLastFailoverStatus failover) => new()
    {
        Id = IdOf(failover.Timestamp, failover.To),
        From = failover.From,
        To = failover.To,
        At = failover.Timestamp,
        ForkLsns = failover.ForkLsns,
    };

    /// <summary>The open divergences of the AG. An AG recorded by an earlier version has no list: its last
    /// failover, when it accepted data loss, stands for one.</summary>
    public static IReadOnlyList<AgDivergenceStatus> Open(SqlAvailabilityGroup? ag)
    {
        if (ag?.Status is not { } status)
            return [];
        if (status.OpenDivergences is { } open)
            return open;
        return status.LastFailover is { DataLoss: true } last ? [Of(last)] : [];
    }

    /// <summary>True when <paramref name="divergence"/> is the last failover of the AG: no failover
    /// happened since, so a replica beyond its fork point got that log from the previous primary.</summary>
    public static bool IsLastFailover(SqlAvailabilityGroup? ag, AgDivergenceStatus divergence)
        => ag?.Status?.LastFailover is { } last && IdOf(last.Timestamp, last.To) == divergence.Id;

    /// <summary>The fork point of <paramref name="divergence"/> for one database, when it was recorded.</summary>
    public static decimal? ForkLsn(AgDivergenceStatus divergence, string database)
        => divergence.ForkLsns?.FirstOrDefault(f => f.Key.Equals(database, StringComparison.OrdinalIgnoreCase)).Value is { } text
           && decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var lsn)
            ? lsn
            : null;
}
