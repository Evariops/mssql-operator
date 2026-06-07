using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

// The database-selection snapshot (per-database phases + selection hash + observedGeneration)
// was part of the removed CRD status tree. Selection is now done live in the controller; this helper
// retains only the pure, status-free utilities (glob matching, missing-name detection).
public static class DatabaseSelector
{
    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <summary>
    /// Resolves the selector against the instance's databases into a sorted, de-duplicated list of
    /// database names. Pure and status-free (replaces the removed BuildSnapshot): the selection is
    /// recomputed live each reconcile rather than persisted.
    /// </summary>
    public static List<string> Select(DatabaseSelectorSpec selector, List<string> allDatabases)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (selector.AllUserDatabases)
            foreach (var d in allDatabases)
                if (!SystemDatabases.Contains(d, StringComparer.OrdinalIgnoreCase))
                    selected.Add(d);

        if (selector.Names is { Count: > 0 })
            foreach (var d in allDatabases)
                if (selector.Names.Contains(d, StringComparer.OrdinalIgnoreCase))
                    selected.Add(d);

        if (selector.NamePatterns is { Count: > 0 })
            foreach (var d in allDatabases)
                if (selector.NamePatterns.Any(p => GlobMatch(d, p)))
                    selected.Add(d);

        if (selector.ExcludeNames is { Count: > 0 })
            selected.RemoveWhere(d => selector.ExcludeNames.Contains(d, StringComparer.OrdinalIgnoreCase));

        if (selector.ExcludePatterns is { Count: > 0 })
            selected.RemoveWhere(d => selector.ExcludePatterns.Any(p => GlobMatch(d, p)));

        if (selector.ExcludeSystemDatabases)
            selected.RemoveWhere(d => SystemDatabases.Contains(d, StringComparer.OrdinalIgnoreCase));

        return selected.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static List<string> GetMissingExplicitNames(DatabaseSelectorSpec selector, List<string> allDatabases)
    {
        if (selector.Names is not { Count: > 0 }) return [];
        return selector.Names
            .Where(n => !allDatabases.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool GlobMatch(string input, string pattern)
    {
        var pIdx = 0;
        var iIdx = 0;
        var starPIdx = -1;
        var starIIdx = -1;

        while (iIdx < input.Length)
        {
            if (pIdx < pattern.Length && (pattern[pIdx] == '?' || char.ToUpperInvariant(pattern[pIdx]) == char.ToUpperInvariant(input[iIdx])))
            {
                pIdx++;
                iIdx++;
            }
            else if (pIdx < pattern.Length && pattern[pIdx] == '*')
            {
                starPIdx = pIdx;
                starIIdx = iIdx;
                pIdx++;
            }
            else if (starPIdx >= 0)
            {
                pIdx = starPIdx + 1;
                starIIdx++;
                iIdx = starIIdx;
            }
            else
            {
                return false;
            }
        }

        while (pIdx < pattern.Length && pattern[pIdx] == '*')
            pIdx++;

        return pIdx == pattern.Length;
    }
}
