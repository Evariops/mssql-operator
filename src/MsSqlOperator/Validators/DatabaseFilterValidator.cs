using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Filters;

namespace MsSqlOperator.Validators;

/// <summary>Admission checks shared by every <see cref="DatabaseFilterSpec"/>.</summary>
public static class DatabaseFilterValidator
{
    /// <summary>Returns an actionable error message, or null when the filter is acceptable.</summary>
    public static string? Validate(DatabaseFilterSpec? filter, string path)
    {
        if (filter is null)
            return null;

        // matchLabels was accepted and silently ignored: the filter then matched more databases than
        // the author asked for. Refused until it is implemented.
        if (filter.MatchLabels is { Count: > 0 })
            return $"{path}.matchLabels is not supported (databases carry no labels); use pattern and exclude";

        if (!string.IsNullOrEmpty(filter.Pattern))
        {
            try
            {
                DatabaseFilter.BuildPatternRegex(filter.Pattern);
            }
            catch (ArgumentException e)
            {
                return $"{path}.pattern is not a valid regular expression: {e.Message}";
            }
        }

        return null;
    }
}
