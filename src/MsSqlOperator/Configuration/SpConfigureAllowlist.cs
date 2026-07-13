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
        // Full-Text crawl/notify bandwidth throttling: pure resource-usage
        // tuning with no code-execution or trust-boundary impact — capping these lets an operator
        // throttle full-text population storms at startup without a restart. Previously classified
        // Unknown (rejected), which made the only config-level FT mitigation inexpressible.
        "ft crawl bandwidth (max)",
        "ft crawl bandwidth (min)",
        "ft notify bandwidth (max)",
        "ft notify bandwidth (min)",
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

    /// <summary>Policy tier of an sp_configure option.</summary>
    public enum Tier
    {
        /// <summary>Tuning option with no code-execution or trust-boundary impact — applies freely.</summary>
        Safe,

        /// <summary>Enables code execution or crosses a trust boundary — requires an explicit,
        /// review-visible allowDangerous opt-in.</summary>
        Dangerous,

        /// <summary>Not recognized — rejected (the list widens on demand, never the other way).</summary>
        Unknown,
    }

    /// <summary>
    /// Classifies an option by tier. Used by BOTH the admission validator and the reconciler
    /// an admission webhook can be disabled or bypassed, so the privileged reconciler
    /// — which actually runs sp_configure — re-applies this same policy at the point of use.
    /// </summary>
    public static Tier Classify(string name) =>
        Safe.Contains(name) ? Tier.Safe
        : Dangerous.Contains(name) ? Tier.Dangerous
        : Tier.Unknown;
}
