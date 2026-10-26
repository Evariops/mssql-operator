namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Marks a spec field as immutable (strict for fields without a
/// safe orchestrated mutation). The codegen emits a CEL rule on the enclosing object so the API
/// server enforces it even with the webhook disabled — value AND presence: a rule on the field
/// itself (<c>self == oldSelf</c>) is only evaluated when both versions carry it, so removing the
/// field and setting it again with another value went through. Validators enforce it too
/// for the admission-time UX message.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ImmutableAttribute(string? reason = null) : Attribute
{
    public string? Reason { get; } = reason;
}

/// <summary>
/// The field may change, but may not be added or removed once the object exists — an optional
/// volume, for instance. Emitted as a CEL rule on the enclosing object.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ImmutablePresenceAttribute(string? reason = null) : Attribute
{
    public string? Reason { get; } = reason;
}
