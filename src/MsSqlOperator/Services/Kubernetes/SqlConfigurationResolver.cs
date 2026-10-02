using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Which SqlConfiguration provides the startup settings (mssql.conf: startup trace flags, default
/// paths, errorlog) of an instance or an availability group. They used to come only from
/// <c>configurationRef</c>, while sp_configure and the global trace flags of a configuration also
/// reached the instances its labels select: the same configuration applied half of itself. A
/// configurationRef wins; otherwise the single configuration selecting the labels applies. Several
/// would each want their own mssql.conf: none applies and the ambiguity is reported.
/// </summary>
public static class SqlConfigurationResolver
{
    public static async Task<(SqlConfiguration? Configuration, string? Ambiguity)> ResolveAsync(
        IKubernetesClient client, string ns, ResourceReference? configurationRef, IDictionary<string, string>? labels, CancellationToken ct)
    {
        if (configurationRef is not null)
            return (await client.GetAsync<SqlConfiguration>(configurationRef.Name, ns, ct), null);

        var matching = (await client.ListAsync<SqlConfiguration>(ns, null, ct))
            .Where(c => c.Metadata.DeletionTimestamp is null && SelectorMatches(c, labels))
            .ToList();
        return matching.Count switch
        {
            0 => (null, null),
            1 => (matching[0], null),
            _ => (null, $"several SqlConfigurations select it by labels ({string.Join(", ", matching.Select(c => c.Metadata.Name).Order())}); " +
                        "their startup settings (mssql.conf) are not applied — set configurationRef to choose one"),
        };
    }

    /// <summary>True when <paramref name="labels"/> satisfy the configuration's selector.
    /// an EMPTY map used to match everything (`All()` on an empty sequence is true); it now matches
    /// nothing — the webhook rejects it outright, and this is the defence in depth.</summary>
    public static bool SelectorMatches(SqlConfiguration config, IDictionary<string, string>? labels)
        => config.Spec.TargetMatchLabels is { Count: > 0 } selector
           && labels != null
           && selector.All(kv => labels.TryGetValue(kv.Key, out var value) && value == kv.Value);
}
