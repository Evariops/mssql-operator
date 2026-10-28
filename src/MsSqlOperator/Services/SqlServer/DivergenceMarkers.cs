using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>One divergence as the database carries it.</summary>
public sealed record DivergenceMarker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("at")] DateTime? At,
    [property: JsonPropertyName("forkLsn")] decimal? ForkLsn);

/// <summary>
/// A divergence lives in the data it concerns — a database-level extended property
/// <c>mssqlop.divergence</c> (a bounded JSON array) on the primary, replicated by the AG with the
/// database, following it across the next failovers. Losing the status of the AGD, recreating it
/// from GitOps, or restoring etcd no longer forgets that a replica holds commits the promotion
/// discarded.
/// </summary>
public static class DivergenceMarkers
{
    public const string PropertyName = "mssqlop.divergence";
    public const int Max = 16;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static IReadOnlyList<DivergenceMarker> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        try { return JsonSerializer.Deserialize<List<DivergenceMarker>>(value, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<DivergenceMarker> markers)
        => JsonSerializer.Serialize(markers.OrderBy(m => m.At).TakeLast(Max).ToList(), Json);

    public static async Task<IReadOnlyList<DivergenceMarker>> ReadAsync(ISqlAgManager agManager, SqlConnection connection, string database,
        CancellationToken ct)
        => Parse(await agManager.ReadDatabasePropertyAsync(connection, database, PropertyName, ct));

    /// <summary>Replaces the markers of <paramref name="database"/> (on the primary); none removes the property.</summary>
    public static Task WriteAsync(ISqlAgManager agManager, SqlConnection connection, string database,
        IReadOnlyCollection<DivergenceMarker> markers, CancellationToken ct)
        => agManager.WriteDatabasePropertyAsync(connection, database, PropertyName, markers.Count == 0 ? null : Serialize(markers), ct);

    public static IReadOnlyList<DivergenceMarker> With(IEnumerable<DivergenceMarker> markers, DivergenceMarker marker)
        => markers.Where(m => m.Id != marker.Id).Append(marker).OrderBy(m => m.At).TakeLast(Max).ToList();

    public static IReadOnlyList<DivergenceMarker> Without(IEnumerable<DivergenceMarker> markers, IEnumerable<string> ids)
    {
        var drop = ids.ToHashSet(StringComparer.Ordinal);
        return markers.Where(m => !drop.Contains(m.Id)).ToList();
    }

    /// <summary>The marker of a status divergence, for one database.</summary>
    public static DivergenceMarker Of(Entities.V1Alpha1.AgDivergenceStatus divergence, string database, long epoch)
        => new(divergence.Id, epoch, divergence.From, divergence.To, divergence.At, AgDivergences.ForkLsn(divergence, database));

    /// <summary>The status form of a marker, so that the AGD reasons on one list.</summary>
    public static Entities.V1Alpha1.AgDivergenceStatus ToStatus(DivergenceMarker marker, string database)
        => new()
        {
            Id = marker.Id,
            From = marker.From,
            To = marker.To,
            At = marker.At,
            ForkLsns = marker.ForkLsn is { } lsn ? new Dictionary<string, string> { [database] = lsn.ToString(System.Globalization.CultureInfo.InvariantCulture) } : null,
        };
}
