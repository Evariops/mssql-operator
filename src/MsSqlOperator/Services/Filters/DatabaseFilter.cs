using System.Text.RegularExpressions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Filters;

public interface IDatabaseFilter
{
    List<string> Apply(List<string> allDatabases, List<string>? explicitDatabases, DatabaseFilterSpec? filter);
}

public partial class DatabaseFilter : IDatabaseFilter
{
    /// <summary>
    /// Two problems with the previous <c>new Regex(pattern)</c>:
    ///
    /// 1. The pattern was NOT anchored, so <c>IsMatch</c> did a substring match: a filter meant for
    ///    <c>app_.*</c> also matched <c>legacy_app_prod</c>, and permissions or stored procedures
    ///    landed on databases the user never intended.
    /// 2. It was compiled with backtracking and no timeout, from a string a CR author controls: a
    ///    catastrophic pattern (<c>(a+)+$</c>) blocks the reconcile worker forever — an unbounded
    ///    denial of service on the operator itself.
    ///
    /// The pattern is now fully anchored (an explicit <c>^…$</c> from the user is preserved) and
    /// evaluated with the non-backtracking engine, which is linear-time by construction.
    /// </summary>
    internal static Regex BuildPatternRegex(string pattern)
    {
        var anchored = pattern;
        if (!anchored.StartsWith('^')) anchored = "^" + anchored;
        if (!anchored.EndsWith('$')) anchored += "$";

        try
        {
            return new Regex(anchored, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // A few constructs (lookarounds, backreferences) are unsupported by NonBacktracking.
            // Fall back to the backtracking engine WITH a timeout — never without one.
            return new Regex(anchored, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
    }

    public List<string> Apply(List<string> allDatabases, List<string>? explicitDatabases, DatabaseFilterSpec? filter)
    {
        if (explicitDatabases is { Count: > 0 })
        {
            return allDatabases.Where(d => explicitDatabases.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (filter == null) return allDatabases;

        var result = allDatabases.AsEnumerable();

        if (!string.IsNullOrEmpty(filter.Pattern))
        {
            var regex = BuildPatternRegex(filter.Pattern);
            result = result.Where(d => regex.IsMatch(d));
        }

        if (filter.MatchLabels is { Count: > 0 })
        {
            // Labels would be resolved from metadata; for now pass through
            // This is a placeholder for label-based filtering
        }

        if (filter.Exclude is { Count: > 0 })
        {
            result = result.Where(d => !filter.Exclude.Contains(d, StringComparer.OrdinalIgnoreCase));
        }

        return result.ToList();
    }
}
