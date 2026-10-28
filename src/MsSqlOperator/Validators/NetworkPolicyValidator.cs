using System.Net;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

/// <summary>
/// An egress rule the builder could not turn into a policy rule would be dropped — the
/// destination unreachable while the CR reads as accepted. Refused at admission instead.
/// </summary>
public static class NetworkPolicyValidator
{
    public static string? Validate(NetworkPolicySpec? spec)
    {
        if (spec?.Egress is not { } rules)
            return null;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var at = $"networkPolicy.egress[{i}]{(rule.Name is { Length: > 0 } n ? $" ({n})" : "")}";
            var kinds = (rule.Fqdns is { Count: > 0 } ? 1 : 0) + (rule.Cidrs is { Count: > 0 } ? 1 : 0)
                        + (rule.Namespace is { Length: > 0 } || rule.PodLabels is { Count: > 0 } ? 1 : 0);
            if (kinds != 1)
                return $"{at}: give exactly one kind of destination — fqdns, cidrs, or namespace/podLabels.";
            if (rule.Ports is not { Count: > 0 } || rule.Ports.Any(p => p is < 1 or > 65535))
                return $"{at}: ports must list at least one TCP port between 1 and 65535.";
            if (rule.Cidrs?.FirstOrDefault(c => !IPNetwork.TryParse(c, out _)) is { } cidr)
                return $"{at}: '{cidr}' is not a CIDR block.";
            if (rule.Fqdns?.FirstOrDefault(f => !IsFqdn(f)) is { } fqdn)
                return $"{at}: '{fqdn}' is not a DNS name (a leading '*.' is the only wildcard).";
        }
        return null;
    }

    private static bool IsFqdn(string name)
    {
        var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        return host.Length is > 0 and <= 253
               && Uri.CheckHostName(host) == UriHostNameType.Dns
               && !host.Contains('*', StringComparison.Ordinal);
    }
}
