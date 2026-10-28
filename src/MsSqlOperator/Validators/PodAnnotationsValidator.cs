using System.Text.RegularExpressions;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Validators;

/// <summary>
/// <c>podAnnotations</c> reach the pod template as they are: a key the API server
/// refuses would fail every apply of the StatefulSet, long after admission, so the key syntax is
/// checked here. The operator's own domain stays the operator's — a user annotation there could
/// pose as the config hash or the template hash the rollouts compare.
/// </summary>
public static partial class PodAnnotationsValidator
{
    public static string? Validate(Dictionary<string, string>? annotations, string field)
    {
        if (annotations is null)
            return null;

        foreach (var key in annotations.Keys)
        {
            var slash = key.LastIndexOf('/');
            var prefix = slash < 0 ? null : key[..slash];
            var name = slash < 0 ? key : key[(slash + 1)..];

            if (prefix is not null && (prefix == OperatorLabels.Group || prefix.EndsWith("." + OperatorLabels.Group, StringComparison.Ordinal)))
                return $"{field}: '{key}' is in the operator's domain ({OperatorLabels.Group}); its annotations are set by the operator only.";

            if (prefix is not null && (prefix.Length > 253 || !DnsSubdomain().IsMatch(prefix)))
                return $"{field}: the prefix of '{key}' must be a DNS subdomain.";

            if (name.Length is 0 or > 63 || !AnnotationName().IsMatch(name))
                return $"{field}: '{key}' is not a valid annotation key (name: up to 63 alphanumerics, '-', '_' or '.', starting and ending with an alphanumeric).";
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$")]
    private static partial Regex DnsSubdomain();

    [GeneratedRegex("^[A-Za-z0-9]([-A-Za-z0-9_.]*[A-Za-z0-9])?$")]
    private static partial Regex AnnotationName();
}
