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

    public WebhookDispatcher(IServiceProvider services, OperatorOptions options)
    {
        _services = services;
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
                "DELETE" => await InvokeAsync(reg, validator, "DeleteAsync",
                    [Deserialize(request.OldObject, reg.EntityType), dryRun, ct]).ConfigureAwait(false),
                _ => new ValidationResult(true),
            };
        }
        catch (Exception e)
        {
            return Respond(uid, allowed: false, 500, $"Validation webhook error: {e.Message}");
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
        var deserialize = typeof(KubernetesJson).GetMethods()
            .First(m => m.Name == "Deserialize" && m.IsGenericMethod
                        && m.GetParameters() is [{ ParameterType.Name: "String" }]);
        return deserialize.MakeGenericMethod(type).Invoke(null, [json]);
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
