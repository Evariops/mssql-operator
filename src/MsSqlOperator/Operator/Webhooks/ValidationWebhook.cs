namespace MsSqlOperator.Operator.Webhooks;

/// <summary>Result of an admission validation, replacing KubeOps' <c>ValidationResult</c>.</summary>
public class ValidationResult(bool valid)
{
    public bool Valid { get; } = valid;

    public string? Message { get; init; }

    /// <summary>HTTP status code returned to the API server on rejection (e.g. 422).</summary>
    public int? StatusCode { get; init; }

    public string[]? Warnings { get; init; }
}

/// <summary>
/// Marks a class as the validating admission webhook for <paramref name="entityType"/>,
/// replacing KubeOps' <c>[ValidationWebhook]</c>. Consumed by the webhook dispatcher and codegen.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ValidationWebhookAttribute(Type entityType) : Attribute
{
    public Type EntityType { get; } = entityType;
}

/// <summary>
/// Base class for validating admission webhooks, replacing KubeOps' <c>ValidationWebhook&lt;T&gt;</c>.
/// Override <see cref="Create"/>/<see cref="Update"/> (or their async variants). There is no
/// Delete hook: deletion protection belongs to finalizers and ownerReferences.
/// </summary>
public abstract class ValidationWebhook<TEntity>
{
    public virtual ValidationResult Create(TEntity entity, bool dryRun) => Success();

    public virtual Task<ValidationResult> CreateAsync(TEntity entity, bool dryRun, CancellationToken ct)
        => Task.FromResult(Create(entity, dryRun));

    public virtual ValidationResult Update(TEntity oldEntity, TEntity newEntity, bool dryRun) => Success();

    public virtual Task<ValidationResult> UpdateAsync(TEntity oldEntity, TEntity newEntity, bool dryRun, CancellationToken ct)
        => Task.FromResult(Update(oldEntity, newEntity, dryRun));

    protected ValidationResult Success(params string[] warnings)
        => new(true) { Warnings = warnings.Length > 0 ? warnings : null };

    protected ValidationResult Fail() => new(false);

    protected ValidationResult Fail(string reason) => new(false) { Message = reason };

    protected ValidationResult Fail(string reason, int statusCode) => new(false) { Message = reason, StatusCode = statusCode };
}
