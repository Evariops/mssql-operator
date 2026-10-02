using System.Text.Json;
using k8s;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Paginated relist: a cluster-wide LIST in one response loads the API server, etcd and the
/// operator's memory in proportion to the fleet. Pages of <see cref="PageSize"/> are fetched with
/// <c>limit</c>/<c>continue</c> and merged back into a single list document, so the watchers keep
/// their JSON-based seeding. All pages come from the snapshot of the first one; an expired continue
/// token (410) fails the relist, which the watcher retries.
/// </summary>
internal static class ListPager
{
    public const int PageSize = 500;

    /// <param name="page">Fetches one page: (continue token or null, ct) → the typed or raw list body.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<string> CollectAsync(Func<string?, CancellationToken, Task<object>> page, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? resourceVersion = null;
        string? continueToken = null;

        do
        {
            var body = await page(continueToken, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(KubernetesJson.Serialize(body));
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var item in array.EnumerateArray())
                    items.Add(item.Clone());

            continueToken = null;
            if (root.TryGetProperty("metadata", out var meta))
            {
                if (resourceVersion is null && meta.TryGetProperty("resourceVersion", out var rv))
                    resourceVersion = rv.GetString();
                if (meta.TryGetProperty("continue", out var next))
                    continueToken = next.GetString();
            }
        }
        while (!string.IsNullOrEmpty(continueToken));

        return JsonSerializer.Serialize(new ListDocument(new ListMetadata(resourceVersion), items));
    }

    private sealed record ListMetadata(
        [property: System.Text.Json.Serialization.JsonPropertyName("resourceVersion")] string? ResourceVersion);

    private sealed record ListDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("metadata")] ListMetadata Metadata,
        [property: System.Text.Json.Serialization.JsonPropertyName("items")] List<JsonElement> Items);
}
