using System.Text.RegularExpressions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Filters;

public interface IDatabaseFilter
{
    List<string> Apply(List<string> allDatabases, List<string>? explicitDatabases, DatabaseFilterSpec? filter);
}

public partial class DatabaseFilter : IDatabaseFilter
{
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
            var regex = new Regex(filter.Pattern, RegexOptions.IgnoreCase);
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
