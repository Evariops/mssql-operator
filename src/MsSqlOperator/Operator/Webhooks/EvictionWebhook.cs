using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Maintenance;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Maintenance;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Operator.Webhooks;

/// <summary>
/// The validating webhook on <c>pods/eviction</c>, served by every replica.
/// For a pod of an AG, it records the request on the pod (never in a dry-run)
/// and, in <c>Enforce</c>, answers with the verdict the leader published there, or a 429 until the
/// deadline. Every other pod, and every path that cannot decide (an API call that fails or exceeds
/// its second, a pod or resource not found, a request that cannot be recorded), is allowed: the
/// webhook delays, it never blocks (principle 1), and its configuration is <c>failurePolicy: Ignore</c>.
/// </summary>
public sealed class EvictionWebhook(
    IKubernetesClient client,
    OperatorOptions options,
    MaintenanceObserver observer,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics,
    TimeProvider timeProvider,
    ILogger<EvictionWebhook> logger)
{
    public const string Path = "/validate/pods-eviction";

    /// <summary>The whole answer stays under the webhook's 3-second timeout.</summary>
    internal static readonly TimeSpan HandlerBudget = TimeSpan.FromMilliseconds(2500);

    /// <summary>Each API call of the handler.</summary>
    internal static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(1);

    public async Task<AdmissionReview> ReviewAsync(AdmissionReview review, CancellationToken ct)
    {
        var request = review.Request;
        var uid = request?.Uid ?? string.Empty;
        if (request is null || options.NodeMaintenance == SwitchMode.Off || request.Operation != "CREATE"
            || string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.Namespace))
            return Respond(uid, EvictionAnswer(true));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(HandlerBudget);
        try
        {
            return Respond(uid, await AnswerAsync(request, request.DryRun == true || IsDryRun(request.Object), budget.Token));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Eviction of pod {Namespace}/{Pod}: no answer within the budget, allowed", request.Namespace, request.Name);
            return Respond(uid, EvictionAnswer(true));
        }
    }

    private async Task<EvictionAnswer> AnswerAsync(AdmissionRequest request, bool dryRun, CancellationToken ct)
    {
        var ns = request.Namespace!;
        var podName = request.Name!;
        var pod = await Bounded(t => client.GetAsync<V1Pod>(podName, ns, t), ct);
        // A SQL pod carries the identity label of its owner, as the owned watch selects it; the
        // StatefulSet template has no managed-by label (found on a test cluster: every SQL pod was allowed).
        var labels = pod?.Metadata?.Labels;
        if (labels is null)
            return EvictionAnswer(true);

        IKubernetesObject<V1ObjectMeta>? resource = labels.TryGetValue(OperatorLabels.Ag, out var ag) && !string.IsNullOrEmpty(ag)
            ? await Bounded(t => client.GetAsync<SqlAvailabilityGroup>(ag, ns, t), ct)
            : labels.TryGetValue(OperatorLabels.Instance, out var instance) && !string.IsNullOrEmpty(instance)
                ? await Bounded(t => client.GetAsync<SqlInstance>(instance, ns, t), ct)
                : null;
        if (resource is null)
            return EvictionAnswer(true);
        var mode = observer.ModeOf(resource);
        if (mode == SwitchMode.Off)
            return EvictionAnswer(true);

        // The request is recorded whatever the mode: it is the Eviction signal of status.maintenance.
        var now = timeProvider.GetUtcNow();
        var current = EvictionAnnotations.RecordOf(pod!.Metadata.Annotations);
        var next = EvictionRequests.Next(current, request.UserInfo?.Username, now);
        var record = current;
        if (next is not null)
        {
            if (dryRun)
                record = next;
            else
            {
                try
                {
                    await Bounded(async t =>
                    {
                        await client.PatchPodAnnotationsAsync(podName, ns, EvictionAnnotations.Patch(next), t);
                        return true;
                    }, ct);
                    record = next;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Eviction of pod {Namespace}/{Pod}: the request could not be recorded", ns, podName);
                    // An abandoned request that could not be renewed would expire at once: start none.
                    record = current is not null && EvictionRequests.IsAbandoned(current, now) ? null : current;
                }
            }
        }

        var kind = resource is SqlAvailabilityGroup ? "availability group" : "instance";
        var name = $"{kind} {ns}/{resource.Metadata.Name}";
        // A SqlInstance is not delayed: for now its pod leaves.
        if (resource is not SqlAvailabilityGroup)
            return EvictionAnswer(true);

        // Shadow: the leader publishes the verdict Enforce would give (status.maintenance, events).
        if (mode != SwitchMode.Enforce)
        {
            if (!dryRun)
                logger.LogInformation("Eviction of pod {Namespace}/{Pod} of the {Resource} recorded and allowed (Shadow)", ns, podName, name);
            return EvictionAnswer(true);
        }

        var released = resource.Metadata.Annotations?.ContainsKey(MaintenanceObserver.ReleaseAnnotation) == true;
        var answer = EvictionRequests.Answer(name, record, EvictionAnnotations.VerdictOf(pod.Metadata.Annotations), released,
            now, options.EvictionBudget);
        if (dryRun)
            return answer;
        if (answer.DeadlineReached)
            await eventPublisher.PublishAsync(resource, "MaintenanceDelayExpired",
                $"The eviction of {podName} goes on: its delay is spent ({answer.Message})", "Warning", ct);
        if (answer.Allowed && record is not null)
            metrics.RecordEvictionDelay(ns, resource.Metadata.Name, (now - record.RequestedAt).TotalSeconds,
                answer.DeadlineReached ? "deadline" : released ? "release" : "verdict");
        logger.Log(answer.Allowed ? LogLevel.Information : LogLevel.Debug, "Eviction of pod {Namespace}/{Pod}: {Answer}",
            ns, podName, answer.Message);
        return answer;
    }

    private static EvictionAnswer EvictionAnswer(bool allowed) => new(allowed, string.Empty);

    /// <summary><c>kubectl drain --dry-run=server</c> puts the dry-run in the Eviction's deleteOptions,
    /// and the admission request then says <c>dryRun: false</c> (VM-02): both are read.</summary>
    internal static bool IsDryRun(JsonElement? eviction)
        => eviction is { ValueKind: JsonValueKind.Object } e
           && e.TryGetProperty("deleteOptions", out var options) && options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty("dryRun", out var dryRun) && dryRun.ValueKind == JsonValueKind.Array && dryRun.GetArrayLength() > 0;

    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallBudget);
        return await call(cts.Token).ConfigureAwait(false);
    }

    private static AdmissionReview Respond(string uid, EvictionAnswer answer)
        => new()
        {
            Response = new AdmissionResponse
            {
                Uid = uid,
                Allowed = answer.Allowed,
                Status = answer.Allowed ? null : new AdmissionStatus { Code = 429, Message = answer.Message },
            },
        };
}
