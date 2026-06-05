using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

public static class TargetSelectorValidator
{
    /// <summary>
    /// Returns an error message if the target selector is invalid, or null if valid.
    /// </summary>
    public static string? Validate(TargetSelector target)
    {
        var hasNamespaces = target.Namespaces is { Count: > 0 };
        var hasLabels = target.MatchLabels is { Count: > 0 };
        var hasInstances = target.Instances is { Count: > 0 };
        var hasAgs = target.AvailabilityGroups is { Count: > 0 };

        if (!hasNamespaces && !hasLabels && !hasInstances && !hasAgs)
            return "target must specify at least one selector (namespaces, matchLabels, instances, or availabilityGroups)";

        // Namespaces alone is too broad — require at least one targeted selector
        if (hasNamespaces && !hasLabels && !hasInstances && !hasAgs)
            return "target with only namespaces is too broad; add matchLabels, instances, or availabilityGroups";

        return null;
    }
}
