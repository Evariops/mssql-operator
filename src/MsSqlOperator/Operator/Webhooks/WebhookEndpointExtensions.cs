using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MsSqlOperator.Operator.Webhooks;

/// <summary>Maps the validating admission webhook endpoints, replacing KubeOps' MVC webhook controllers.</summary>
public static class WebhookEndpointExtensions
{
    public static IEndpointRouteBuilder MapValidationWebhooks(this IEndpointRouteBuilder endpoints)
    {
        // A literal route, matched before /validate/{kind}.
        endpoints.MapPost(EvictionWebhook.Path, async (HttpRequest request, EvictionWebhook webhook, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            AdmissionReview review;
            try
            {
                review = KubernetesJson.Deserialize<AdmissionReview>(body);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
            {
                return Results.BadRequest("Invalid AdmissionReview payload.");
            }
            return Results.Text(KubernetesJson.Serialize(await webhook.ReviewAsync(review, ct)), "application/json");
        });

        endpoints.MapPost("/validate/{kind}", async (
            string kind, HttpRequest request, WebhookDispatcher dispatcher, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);

            AdmissionReview review;
            try
            {
                review = KubernetesJson.Deserialize<AdmissionReview>(body);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
            {
                return Results.BadRequest("Invalid AdmissionReview payload.");
            }

            var response = await dispatcher.ValidateAsync(kind, review, ct);
            return Results.Text(KubernetesJson.Serialize(response), "application/json");
        });

        return endpoints;
    }
}
