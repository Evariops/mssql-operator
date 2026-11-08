using System.Reflection;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using MsSqlOperator.Operator.Registration;

namespace MsSqlOperator.Operator.Webhooks;

/// <summary>
/// Routes admission requests to the matching <see cref="ValidationWebhook{T}"/>, replacing
/// KubeOps' generated webhook endpoints. Webhooks are keyed by the lower-cased entity kind,
/// matching the path used in the ValidatingWebhookConfiguration (e.g. <c>/validate/sqlinstance</c>).
/// </summary>
public sealed class WebhookDispatcher
{
    private sealed record Registration(Type ValidatorType, Type EntityType);

    private readonly Dictionary<string, Registration> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _services;
    private readonly Microsoft.Extensions.Logging.ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(IServiceProvider services, OperatorOptions options,
        Microsoft.Extensions.Logging.ILogger<WebhookDispatcher> logger)
    {
        _services = services;
        _logger = logger;
        foreach (var asm in options.Assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                var attr = type.GetCustomAttribute<ValidationWebhookAttribute>();
                if (attr is null || type.IsAbstract)
                    continue;
                var entityAttr = attr.EntityType.GetCustomAttribute<KubernetesEntityAttribute>()
                    ?? throw new InvalidOperationException($"{attr.EntityType.Name} is missing [KubernetesEntity].");
                _byPath[entityAttr.Kind.ToLowerInvariant()] = new Registration(type, attr.EntityType);
            }
        }
    }

    public IReadOnlyCollection<string> Paths => _byPath.Keys;

    public async Task<AdmissionReview> ValidateAsync(string path, AdmissionReview review, CancellationToken ct)
    {
        var request = review.Request;
        var uid = request?.Uid ?? string.Empty;

        if (request is null || !_byPath.TryGetValue(path, out var reg))
            return Respond(uid, allowed: false, 404, $"No validating webhook registered for '{path}'.");

        using var scope = _services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService(reg.ValidatorType);
        var dryRun = request.DryRun ?? false;

        ValidationResult result;
        try
        {
            result = request.Operation switch
            {
                "CREATE" => await InvokeAsync(reg, validator, "CreateAsync",
                    [Deserialize(request.Object, reg.EntityType), dryRun, ct]).ConfigureAwait(false),
                "UPDATE" => await InvokeAsync(reg, validator, "UpdateAsync",
                    [Deserialize(request.OldObject, reg.EntityType), Deserialize(request.Object, reg.EntityType), dryRun, ct]).ConfigureAwait(false),
                // No DELETE branch — it was never declared in
                // the webhook's operations: and gave a false impression of coverage. Deletion
                // protection belongs to finalizers/ownerReferences. Reintroduce atomically
                // (code + operations: declaration + e2e test) if a real need appears.
                // Known bypasses: objects created before the webhook installation, and
                // updates via the /status subresource (rules do not match subresources).
                _ => new ValidationResult(true),
            };
        }
        catch (TargetInvocationException tie) when (tie.InnerException is ArgumentException e and not ArgumentNullException)
        {
            // Validators are pure functions of the submitted object; an ArgumentException
            // escaping one (a SqlQuoting value check) is an invalid input, not a server fault.
            return Respond(uid, allowed: false, 422, e.Message);
        }
        catch (ArgumentException e) when (e is not ArgumentNullException)
        {
            return Respond(uid, allowed: false, 422, e.Message);
        }
        catch (Exception e)
        {
            // The raw exception text may leak internals to any CR author — keep the client
            // message generic, the detail goes to the operator logs.
            //
            // "the operator logs" is only true now. This used to write to
            // System.Diagnostics.Trace, for which nothing is registered as a listener — so the detail
            // went precisely nowhere. With failurePolicy: Fail, a validator throwing on every request
            // blocks all CRD writes, and the one message explaining why was silently discarded.
            _logger.LogError(e, "Validation webhook error for {Operation} on {Path}",
                LogValues.OneLine(request.Operation), LogValues.OneLine(path));
            return Respond(uid, allowed: false, 500, "internal validation error; see operator logs");
        }

        return Respond(uid, result.Valid, result.StatusCode ?? (result.Valid ? 200 : 400), result.Message, result.Warnings);
    }

    private static async Task<ValidationResult> InvokeAsync(Registration reg, object validator, string method, object?[] args)
    {
        var mi = reg.ValidatorType.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{reg.ValidatorType.Name}.{method} not found.");
        var task = (Task<ValidationResult>)mi.Invoke(validator, args)!;
        return await task.ConfigureAwait(false);
    }

    private static object? Deserialize(System.Text.Json.JsonElement? raw, Type type)
    {
        if (raw is not { } element)
            return null;
        var json = element.GetRawText();
        // KubernetesJson.Deserialize<T> is `(string json, JsonSerializerOptions? options = null)`
        // in KubernetesClient 19.x: match on the first string parameter and fill any trailing
        // optional parameters with Type.Missing so the overload resolves at runtime.
        var deserialize = typeof(KubernetesJson).GetMethods()
            .First(m => m.Name == "Deserialize" && m.IsGenericMethod
                        && m.GetParameters() is [{ ParameterType.Name: "String" }, ..]);
        var parameters = deserialize.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = json;
        for (var i = 1; i < parameters.Length; i++)
            args[i] = Type.Missing;
        return deserialize.MakeGenericMethod(type).Invoke(null, args);
    }

    private static AdmissionReview Respond(string uid, bool allowed, int code, string? message, string[]? warnings = null)
        => new()
        {
            Response = new AdmissionResponse
            {
                Uid = uid,
                Allowed = allowed,
                Warnings = warnings,
                Status = allowed && message is null
                    ? null
                    : new AdmissionStatus { Code = code, Message = message },
            },
        };
}
