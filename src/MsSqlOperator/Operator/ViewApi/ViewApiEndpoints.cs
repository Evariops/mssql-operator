using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Serves the aggregated, read-only <c>view.mssql-operator.evariops.com/v1alpha1</c> API: discovery + list/get for
/// each registered <see cref="IViewResourceProvider"/>, computed live from SQL. Registered behind a
/// Kubernetes APIService so plain <c>kubectl get</c> works (docs/specifications/16). No watch, no writes.
/// </summary>
public static class ViewApiEndpoints
{
    public const string GroupVersion = "view.mssql-operator.evariops.com/v1alpha1";

    public static IEndpointRouteBuilder MapViewApi(this IEndpointRouteBuilder endpoints)
    {
        // ── Discovery ──────────────────────────────────────────
        endpoints.MapGet("/apis/view.mssql-operator.evariops.com", (IEnumerable<IViewResourceProvider> _) =>
            Json(new ApiGroupList
            {
                Groups =
                [
                    new ApiGroup
                    {
                        Name = "view.mssql-operator.evariops.com",
                        Versions = [new GroupVersionForDiscovery { GroupVersion = GroupVersion, Version = "v1alpha1" }],
                        PreferredVersion = new GroupVersionForDiscovery { GroupVersion = GroupVersion, Version = "v1alpha1" },
                    },
                ],
            }));

        endpoints.MapGet("/apis/view.mssql-operator.evariops.com/v1alpha1", (IEnumerable<IViewResourceProvider> providers) =>
            Json(new ApiResourceList
            {
                GroupVersion = GroupVersion,
                Resources = providers.Select(p => p.Resource).ToList(),
            }));

        // ── OpenAPI discovery ──────────────────────────────────
        // The kube-aggregator probes /openapi/v2 and /openapi/v3 of every aggregated apiserver. We serve
        // minimal-but-valid documents: kubectl already prints via server-side Tables, so no schema detail
        // is needed — these exist to satisfy the aggregator and silence its discovery warnings.
        endpoints.MapGet("/openapi/v2", () => Json(new Dictionary<string, object?>
        {
            ["swagger"] = "2.0",
            ["info"] = new Dictionary<string, object?> { ["title"] = "MsSqlOperator view API", ["version"] = "v1alpha1" },
            ["paths"] = new Dictionary<string, object?>(),
        }));

        endpoints.MapGet("/openapi/v3", () => Json(new Dictionary<string, object?>
        {
            ["paths"] = new Dictionary<string, object?>
            {
                ["apis/view.mssql-operator.evariops.com/v1alpha1"] = new Dictionary<string, object?>
                {
                    ["serverRelativeURL"] = "/openapi/v3/apis/view.mssql-operator.evariops.com/v1alpha1",
                },
            },
        }));

        endpoints.MapGet("/openapi/v3/apis/view.mssql-operator.evariops.com/v1alpha1", () => Json(new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.0",
            ["info"] = new Dictionary<string, object?> { ["title"] = "view.mssql-operator.evariops.com", ["version"] = "v1alpha1" },
            ["paths"] = new Dictionary<string, object?>(),
            ["components"] = new Dictionary<string, object?>(),
        }));

        // ── List (all namespaces) ──────────────────────────────
        endpoints.MapGet("/apis/view.mssql-operator.evariops.com/v1alpha1/{resource}", async (
            string resource, HttpContext ctx, IEnumerable<IViewResourceProvider> providers,
            AggregatorAuthorizer auth, CancellationToken ct) =>
            await ListAsync(resource, null, null, ctx, providers, auth, ct));

        // ── List (namespaced) ──────────────────────────────────
        endpoints.MapGet("/apis/view.mssql-operator.evariops.com/v1alpha1/namespaces/{ns}/{resource}", async (
            string ns, string resource, HttpContext ctx, IEnumerable<IViewResourceProvider> providers,
            AggregatorAuthorizer auth, CancellationToken ct) =>
            await ListAsync(resource, ns, null, ctx, providers, auth, ct));

        // ── Get (namespaced) ───────────────────────────────────
        endpoints.MapGet("/apis/view.mssql-operator.evariops.com/v1alpha1/namespaces/{ns}/{resource}/{name}", async (
            string ns, string resource, string name, HttpContext ctx, IEnumerable<IViewResourceProvider> providers,
            AggregatorAuthorizer auth, CancellationToken ct) =>
            await ListAsync(resource, ns, name, ctx, providers, auth, ct));

        return endpoints;
    }

    private static async Task<IResult> ListAsync(string resource, string? ns, string? name, HttpContext ctx,
        IEnumerable<IViewResourceProvider> providers, AggregatorAuthorizer auth, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Resource.Name == resource);
        if (provider is null)
            return Results.NotFound();

        // Unsupported list options answer explicitly instead of being ignored.
        if (ctx.Request.Query.TryGetValue("watch", out var watch) && watch == "true")
            return Results.Text("watch is not supported by the view API", "text/plain", statusCode: 400);

        var verb = name is null ? "list" : "get";
        var (allowed, status, reason) = await auth.AuthorizeAsync(ctx, resource, verb, ns, ct);
        if (!allowed)
            return Results.Text(reason ?? "forbidden", "text/plain", statusCode: status);

        // TTL cache + bounded SQL fan-out. The whole (resource, ns) listing is cached;
        // a single-name get filters the cached list.
        var gate = ctx.RequestServices.GetRequiredService<ViewQueryGate>();
        var fetched = await gate.GetOrFetchAsync(resource, ns, () => provider.ListAsync(ns, null, ct), ct);
        if (fetched is null)
        {
            ctx.Response.Headers.RetryAfter = "5";
            return Results.Text("view API is saturated; retry shortly", "text/plain", statusCode: 429);
        }

        var items = fetched;
        if (name is not null)
            items = items.Where(i =>
                i.GetValueOrDefault("metadata") is Dictionary<string, object?> md
                && md.GetValueOrDefault("name") as string == name).ToList();

        // Honour limit/continue (opaque offset token) instead of always materializing.
        if (name is null && ctx.Request.Query.TryGetValue("limit", out var limitStr)
            && int.TryParse(limitStr, out var limit) && limit > 0)
        {
            var offset = 0;
            if (ctx.Request.Query.TryGetValue("continue", out var contStr))
                int.TryParse(contStr, out offset);

            var page = items.Skip(offset).Take(limit).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < items.Count;
            if (WantsTable(ctx.Request.Headers["Accept"]))
            {
                var pagedTable = new Table
                {
                    ColumnDefinitions = provider.Columns.ToList(),
                    Rows = page.Select(i =>
                    {
                        var row = SanitizeRow(provider.ToRow(i));
                        row.Object = PartialObjectMetadata(i);
                        return row;
                    }).ToList(),
                };
                if (hasMore) pagedTable.Metadata = new ListMeta { ContinueToken = nextOffset.ToString() };
                return Json(pagedTable);
            }
            var list = new ViewList { Kind = provider.Resource.Kind + "List", Items = page };
            if (hasMore) list.Metadata.ContinueToken = nextOffset.ToString();
            return Json(list);
        }

        // Server-side printing for `kubectl get`.
        if (WantsTable(ctx.Request.Headers["Accept"]))
        {
            var table = new Table
            {
                ColumnDefinitions = provider.Columns.ToList(),
                Rows = items.Select(i =>
                {
                    // Sanitize on this (non-paginated) branch too — a hostile SQL-sourced
                    // name must not inject terminal escapes into `kubectl get` regardless of paging.
                    var row = SanitizeRow(provider.ToRow(i));
                    row.Object = PartialObjectMetadata(i);
                    return row;
                }).ToList(),
            };
            return Json(table);
        }

        if (name is not null)
            return items.Count > 0 ? Json(items[0]) : Results.NotFound();

        return Json(new ViewList { Kind = provider.Resource.Kind + "List", Items = items });
    }

    /// <summary>Table cells carry raw SQL values (replica/login/database names);
    /// strip control characters so a hostile name cannot inject terminal escapes via kubectl.</summary>
    private static TableRow SanitizeRow(TableRow row)
    {
        for (var i = 0; i < row.Cells.Count; i++)
            if (row.Cells[i] is string s && s.Any(char.IsControl))
                row.Cells[i] = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return row;
    }

    private static bool WantsTable(string? accept) =>
        accept is not null && accept.Contains("as=Table", StringComparison.OrdinalIgnoreCase);

    // Minimal PartialObjectMetadata so `kubectl get -A` can fill the NAMESPACE column from each row.
    private static Dictionary<string, object?> PartialObjectMetadata(Dictionary<string, object?> item) => new()
    {
        ["kind"] = "PartialObjectMetadata",
        ["apiVersion"] = "meta.k8s.io/v1",
        ["metadata"] = item.GetValueOrDefault("metadata") ?? new Dictionary<string, object?>(),
    };

    private static IResult Json(object value) =>
        Results.Text(ViewApiJson.Serialize(value), "application/json");
}
