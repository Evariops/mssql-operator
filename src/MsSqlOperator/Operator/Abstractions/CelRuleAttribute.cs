namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// A CEL rule (<c>x-kubernetes-validations</c>) on the object schema of the class it marks — a
/// transition rule when it reads <c>oldSelf</c>. The API server enforces it on every write, whoever
/// the writer: a rule here is a guarantee, not a convention.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CelRuleAttribute(string rule, string message) : Attribute
{
    public string Rule { get; } = rule;
    public string Message { get; } = message;
}
