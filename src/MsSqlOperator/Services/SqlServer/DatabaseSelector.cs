using System.Security.Cryptography;
using System.Text;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

public static class DatabaseSelector
{
    private static readonly HashSet<string> SystemDatabases =
        new(["master", "model", "msdb", "tempdb"], StringComparer.OrdinalIgnoreCase);

    public static DatabaseSelectionSnapshotStatus BuildSnapshot(
        DatabaseSelectorSpec selector,
        List<string> allDatabases,
        long observedGeneration)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (selector.AllUserDatabases)
        {
            foreach (var db in allDatabases)
                selected.Add(db);
        }

        if (selector.Names is { Count: > 0 })
        {
            foreach (var name in selector.Names)
            {
                if (allDatabases.Contains(name, StringComparer.OrdinalIgnoreCase))
                    selected.Add(name);
            }
        }

        if (selector.NamePatterns is { Count: > 0 })
        {
            foreach (var pattern in selector.NamePatterns)
            {
                foreach (var db in allDatabases)
                {
                    if (GlobMatch(db, pattern))
                        selected.Add(db);
                }
            }
        }

        if (selector.ExcludeNames is { Count: > 0 })
        {
            foreach (var name in selector.ExcludeNames)
                selected.Remove(name);
        }

        if (selector.ExcludePatterns is { Count: > 0 })
        {
            foreach (var pattern in selector.ExcludePatterns)
                selected.RemoveWhere(db => GlobMatch(db, pattern));
            }

        if (selector.ExcludeSystemDatabases)
        {
            selected.RemoveWhere(db => SystemDatabases.Contains(db));
        }

        var sorted = selected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var hash = ComputeHash(sorted);

        return new DatabaseSelectionSnapshotStatus
        {
            SelectedAt = DateTime.UtcNow,
            ObservedGeneration = observedGeneration,
            SelectionHash = hash,
            Databases = sorted.Select(name => new DatabaseRotationStatus { Name = name }).ToList()
        };
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

    private static string ComputeHash(List<string> names)
    {
        var joined = string.Join('\n', names);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
