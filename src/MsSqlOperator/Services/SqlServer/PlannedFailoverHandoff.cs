namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Whether the target of a planned failover holds the whole log of the previous primary,
/// read once the primary is offline. Each replica answers for its own copy: the row of its own name
/// in <c>sys.dm_hadr_database_replica_states</c>.
/// </summary>
public static class PlannedFailoverHandoff
{
    public sealed record Result(bool Verified, string? Gap);

    public static Result Compare(IReadOnlyCollection<string> databases,
        IReadOnlyList<AgDatabaseStateInfo> previousPrimaryRows, string previousPrimary,
        IReadOnlyList<AgDatabaseStateInfo> targetRows, string target)
    {
        var gaps = new List<string>();
        foreach (var database in databases)
        {
            var source = LocalLsn(previousPrimaryRows, previousPrimary, database);
            var copy = LocalLsn(targetRows, target, database);
            if (source is null)
                gaps.Add($"{database}: no log position on {previousPrimary}");
            else if (copy is null)
                gaps.Add($"{database}: no log position on {target}");
            else if (copy < source)
                gaps.Add($"{database}: hardened up to {copy} on {target}, {source} on {previousPrimary}");
        }
        return gaps.Count == 0 ? new Result(true, null) : new Result(false, string.Join("; ", gaps));
    }

    private static decimal? LocalLsn(IReadOnlyList<AgDatabaseStateInfo> rows, string replica, string database)
        => rows.Where(r => r.ReplicaName.Equals(replica, StringComparison.OrdinalIgnoreCase)
                           && r.DatabaseName.Equals(database, StringComparison.OrdinalIgnoreCase)
                           && r.LastHardenedLsn is not null)
            .Select(r => r.LastHardenedLsn)
            .FirstOrDefault();
}
