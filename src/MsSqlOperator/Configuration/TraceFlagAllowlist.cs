namespace MsSqlOperator.Configuration;

/// <summary>
/// Global/startup trace flags the operator is allowed to manage.
///
/// Trace flags change engine behaviour globally and some of them are genuinely dangerous (disabling
/// crash-recovery checks, forcing recovery modes, dumping internals). The controller also applies a
/// TOTAL reconciliation — it turns OFF every active flag that is not in the spec — so an arbitrary
/// number here was a loaded gun in both directions. Only well-known, documented flags pass, and the
/// controller now only ever disables flags it enabled itself.
/// </summary>
public static class TraceFlagAllowlist
{
    /// <summary>Documented, supportable flags. Each entry is a deliberate decision.</summary>
    public static readonly IReadOnlySet<int> Allowed = new HashSet<int>
    {
        1117, // grow all files in a filegroup together (tempdb best practice pre-2016)
        1118, // full extents only (tempdb contention, pre-2016)
        1204, // deadlock information to the errorlog
        1222, // deadlock information in XML
        1224, // disable lock escalation by row count
        2371, // dynamic auto-update statistics threshold (pre-2016)
        3226, // suppress successful-backup messages in the errorlog
        3625, // trim the detail of error messages sent to clients
        4199, // query optimizer fixes
        7412, // lightweight query execution statistics profiling
        8048, // partition memory objects by CPU
        9024, // convert the global log pool memory object to a partitioned one
        460,  // detailed data truncation message (string or binary data would be truncated)
        2528, // disable parallel checking of objects by DBCC
        6498, // allow additional large-query memory grants for columnstore builds
        834,  // large-page allocations for the buffer pool
        876,  // large-page allocations for columnstore
        3979, // honour FUA (forced unit access) semantics
        3982, // disable FUA
    };

    public static bool IsAllowed(int flag) => Allowed.Contains(flag);
}
