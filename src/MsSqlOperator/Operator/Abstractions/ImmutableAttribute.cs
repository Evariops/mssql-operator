namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Marks a spec field as immutable (strict for fields without a
/// safe orchestrated mutation). The codegen emits a CEL rule (<c>self == oldSelf</c>) so the
/// API server enforces it even with the webhook disabled; validators enforce it too for the
/// admission-time UX message.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ImmutableAttribute(string? reason = null) : Attribute
{
    public string? Reason { get; } = reason;
}
