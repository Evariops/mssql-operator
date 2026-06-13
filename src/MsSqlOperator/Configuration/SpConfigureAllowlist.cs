namespace MsSqlOperator.Configuration;

/// <summary>
/// Allowlist for spConfigure.advanced[]. Three tiers:
/// safe tuning options pass; dangerous options require allowDangerous: true in the CR
/// (greppable, visible in review); anything else is rejected — the list widens on demand,
/// never the other way. Keep in sync with new SQL Server versions here, in one place.
/// </summary>
public static class SpConfigureAllowlist
{
    /// <summary>Tuning options with no code-execution or trust-boundary impact.</summary>
    public static readonly HashSet<string> Safe = new(StringComparer.OrdinalIgnoreCase)
    {
        "max degree of parallelism",
        "cost threshold for parallelism",
        "optimize for ad hoc workloads",
        "backup compression default",
        "backup checksum default",
        "fill factor (%)",
        "max worker threads",
        "query wait (s)",
        "recovery interval (min)",
        "remote query timeout (s)",
        "default trace enabled",
        "blocked process threshold (s)",
        "cursor threshold",
        "max server memory (MB)",
        "min server memory (MB)",
        "tempdb metadata memory-optimized",
    };

    /// <summary>Options that enable code execution or cross trust boundaries — a CR that can
    /// set these can take over the host; they demand a deliberate, visible opt-in.</summary>
    public static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        "xp_cmdshell",
        "clr enabled",
        "clr strict security",
        "Ad Hoc Distributed Queries",
        "Ole Automation Procedures",
        "external scripts enabled",
        "cross db ownership chaining",
        "remote access",
        "remote admin connections",
    };
}
