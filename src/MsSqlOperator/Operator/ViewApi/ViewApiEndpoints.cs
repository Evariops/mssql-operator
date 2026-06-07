using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Serves the aggregated, read-only <c>view.mssql.operator/v1alpha1</c> API: discovery + list/get for
/// each registered <see cref="IViewResourceProvider"/>, computed live from SQL. Registered behind a
/// Kubernetes APIService so plain <c>kubectl get</c> works (specs/16). No watch, no writes.
/// </summary>
public static class ViewApiEndpoints
{
    public const string GroupVersion = "view.mssql.operator/v1alpha1";

    public static IEndpointRouteBuilder MapViewApi(this IEndpointRouteBuilder endpoints)
    {
        // ── Discovery ──────────────────────────────────────────
        endpoints.MapGet("/apis/view.mssql.operator", (IEnumerable<IViewResourceProvider> _) =>
            Json(new ApiGroupList
            {
                Groups =
                [
                    new ApiGroup
                    {
                        Name = "view.mssql.operator",
                        Versions = [new GroupVersionForDiscovery { GroupVersion = GroupVersion, Version = "v1alpha1" }],
                        PreferredVersion = new GroupVersionForDiscovery { GroupVersion = GroupVersion, Version = "v1alpha1" },
                    },
                ],
            }));

        endpoints.MapGet("/apis/view.mssql.operator/v1alpha1", (IEnumerable<IViewResourceProvider> providers) =>
            Json(new ApiResourceList
            {
                GroupVersion = GroupVersion,
                Resources = providers.Select(p => p.Resource).ToList(),
            }));

        // ── List (all namespaces) ──────────────────────────────
        endpoints.MapGet("/apis/view.mssql.operator/v1alpha1/{resource}", async (
            string resource, HttpContext ctx, IEnumerable<IViewResourceProvider> providers,
            AggregatorAuthorizer auth, CancellationToken ct) =>
            await ListAsync(resource, null, null, ctx, providers, auth, ct));

        // ── List (namespaced) ──────────────────────────────────
        endpoints.MapGet("/apis/view.mssql.operator/v1alpha1/namespaces/{ns}/{resource}", async (
            string ns, string resource, HttpContext ctx, IEnumerable<IViewResourceProvider> providers,
            AggregatorAuthorizer auth, CancellationToken ct) =>
            await ListAsync(resource, ns, null, ctx, providers, auth, ct));

        // ── Get (namespaced) ───────────────────────────────────
        endpoints.MapGet("/apis/view.mssql.operator/v1alpha1/namespaces/{ns}/{resource}/{name}", async (
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

        var verb = name is null ? "list" : "get";
        var (allowed, status, reason) = await auth.AuthorizeAsync(ctx, resource, verb, ns, ct);
        if (!allowed)
            return Results.Text(reason ?? "forbidden", "text/plain", statusCode: status);

        var items = await provider.ListAsync(ns, name, ct);

        // Server-side printing for `kubectl get`.
        if (WantsTable(ctx.Request.Headers["Accept"]))
        {
            var table = new Table
            {
                ColumnDefinitions = provider.Columns.ToList(),
                Rows = items.Select(provider.ToRow).ToList(),
            };
            return Json(table);
        }

        if (name is not null)
            return items.Count > 0 ? Json(items[0]) : Results.NotFound();

        return Json(new ViewList { Kind = provider.Resource.Kind + "List", Items = items });
    }

    private static bool WantsTable(string? accept) =>
        accept is not null && accept.Contains("as=Table", StringComparison.OrdinalIgnoreCase);

    private static IResult Json(object value) =>
        Results.Text(ViewApiJson.Serialize(value), "application/json");
}
