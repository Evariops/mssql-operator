using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

public static class TargetSelectorValidator
{
    /// <summary>
    /// Returns an error message if the target selector is invalid, or null if valid.
    /// </summary>
    public static string? Validate(TargetSelector target)
    {
        var hasLabels = target.MatchLabels is { Count: > 0 };
        var hasInstances = target.Instances is { Count: > 0 };
        var hasAgs = target.AvailabilityGroups is { Count: > 0 };

        if (!hasLabels && !hasInstances && !hasAgs)
            return "target must specify at least one selector (matchLabels, instances, or availabilityGroups)";

        return null;
    }
}
