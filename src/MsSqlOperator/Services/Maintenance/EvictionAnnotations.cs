using System.Globalization;
using System.Text.Json;
using MsSqlOperator.Core.Maintenance;

namespace MsSqlOperator.Services.Maintenance;

/// <summary>
/// The annotations an eviction request leaves on a pod — written by the webhook (the request)
/// and by the leader (the verdict) — and their parsing. A value that does not parse counts as absent:
/// the webhook then treats the request as new, or the verdict as missing.
/// </summary>
public static class EvictionAnnotations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record VerdictJson(string Decision, string Reason, DateTimeOffset Deadline, DateTimeOffset ValidUntil);

    public static string Stamp(DateTimeOffset time)
        => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static EvictionRecord? RecordOf(IDictionary<string, string>? annotations)
    {
        if (annotations is null || !annotations.TryGetValue(MaintenanceSignals.RequestedAtAnnotation, out var at) || Time(at) is not { } requested)
            return null;
        var last = annotations.TryGetValue(MaintenanceSignals.LastRequestAtAnnotation, out var l) ? Time(l) : null;
        return new EvictionRecord(requested, annotations.TryGetValue(MaintenanceSignals.RequestedByAnnotation, out var by) ? by : null,
            last is { } t && t > requested ? t : requested);
    }

    /// <summary>The annotations of <paramref name="record"/>, for a merge patch.</summary>
    public static Dictionary<string, string?> Patch(EvictionRecord record) => new()
    {
        [MaintenanceSignals.RequestedAtAnnotation] = Stamp(record.RequestedAt),
        [MaintenanceSignals.RequestedByAnnotation] = record.RequestedBy,
        [MaintenanceSignals.LastRequestAtAnnotation] = Stamp(record.LastRequestAt),
    };

    /// <summary>A merge patch that removes the request and its verdict.</summary>
    public static Dictionary<string, string?> Clear()
        => MaintenanceSignals.RequestAnnotations.ToDictionary(a => a, _ => (string?)null);

    public static string Serialize(PublishedVerdict verdict)
        => JsonSerializer.Serialize(new VerdictJson(verdict.Decision.ToString(), verdict.Reason, verdict.Deadline, verdict.ValidUntil), Json);

    public static PublishedVerdict? VerdictOf(IDictionary<string, string>? annotations)
    {
        if (annotations is null || !annotations.TryGetValue(MaintenanceSignals.VerdictAnnotation, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var v = JsonSerializer.Deserialize<VerdictJson>(raw, Json);
            return v is not null && Enum.TryParse<VerdictDecision>(v.Decision, ignoreCase: true, out var decision)
                ? new PublishedVerdict(decision, v.Reason ?? string.Empty, v.Deadline, v.ValidUntil)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Time(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
