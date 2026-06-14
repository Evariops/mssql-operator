using System.Text.RegularExpressions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

/// <summary>
/// Static admission validation for <see cref="TopologySpreadSpec"/> + <c>spec.pool</c>.
/// Returns an error message (null = valid) and appends non-blocking admission warnings.
/// Cannot validate the runtime footgun (replicas &gt; domains under DoNotSchedule) — that is surfaced
/// at reconcile time, not admission.
/// </summary>
public static partial class TopologySpreadValidator
{
    public static string? Validate(
        TopologySpreadSpec? spread, AntiAffinitySpec? antiAffinity, string? pool, bool isSinglePod, List<string> warnings)
    {
        if (!string.IsNullOrEmpty(pool) && !Rfc1123Label().IsMatch(pool))
            return "spec.pool must be a valid RFC1123 label (lowercase alphanumeric, '-', '.', '_', max 63 chars)";

        if (spread == null) return null;

        if (spread.Self is { } self)
        {
            if (ValidateRule("self", self) is { } e) return e;
            if (isSinglePod)
                warnings.Add("topologySpread.self has no effect on a single-pod SqlInstance (one pod is trivially balanced)");
        }

        if (spread.Pool is { } poolRule)
        {
            if (string.IsNullOrEmpty(pool)) return "topologySpread.pool requires spec.pool to be set";
            if (ValidateRule("pool", poolRule) is { } e) return e;
        }

        if (spread.PoolAntiAffinity is { } paa)
        {
            if (string.IsNullOrEmpty(pool)) return "topologySpread.poolAntiAffinity requires spec.pool to be set";
            if (!IsMode(paa)) return "topologySpread.poolAntiAffinity must be 'Required' or 'Preferred'";
        }

        if (spread.Constraints is { } raw)
        {
            foreach (var c in raw)
            {
                if (string.IsNullOrWhiteSpace(c.TopologyKey))
                    return "topologySpread.constraints[].topologyKey is required";
                if (c.MaxSkew < 1)
                    return "topologySpread.constraints[].maxSkew must be >= 1";
                if (c.WhenUnsatisfiable is not ("DoNotSchedule" or "ScheduleAnyway"))
                    return "topologySpread.constraints[].whenUnsatisfiable must be 'DoNotSchedule' or 'ScheduleAnyway'";
            }
        }

        // Both the hostname anti-affinity and a hostname self-spread target the same key: legal but
        // redundant (anti-affinity is stricter). Warn so the user picks one.
        if (IsRequired(spread.Self?.Node)
            && antiAffinity?.RequiredSelfAntiAffinity?.TopologyKey == "kubernetes.io/hostname")
            warnings.Add("antiAffinity.requiredSelfAntiAffinity and topologySpread.self.node both target the hostname; prefer one");

        return null;
    }

    private static string? ValidateRule(string path, SpreadRule rule)
    {
        if (rule.Node is { } node && !IsMode(node))
            return $"topologySpread.{path}.node must be 'Required' or 'Preferred'";
        if (rule.Zone is { } zone && !IsMode(zone))
            return $"topologySpread.{path}.zone must be 'Required' or 'Preferred'";
        if (rule.MaxSkew < 1)
            return $"topologySpread.{path}.maxSkew must be >= 1";
        if (rule.MinDomains is { } md)
        {
            if (md < 1) return $"topologySpread.{path}.minDomains must be >= 1";
            if (!IsRequired(rule.Node) && !IsRequired(rule.Zone))
                return $"topologySpread.{path}.minDomains only applies with a Required rule";
        }
        return null;
    }

    private static bool IsMode(string mode) =>
        mode.Equals("Required", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("Preferred", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequired(string? mode) =>
        mode != null && mode.Equals("Required", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[a-z0-9A-Z]([a-z0-9A-Z\-_.]{0,61}[a-z0-9A-Z])?$")]
    private static partial Regex Rfc1123Label();
}
