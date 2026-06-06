namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Declares short names for a custom resource (e.g. <c>sagdb</c>), replacing KubeOps'
/// <c>KubernetesEntityShortNames</c>. Consumed by the CRD code generator
/// (<c>spec.names.shortNames</c>); no runtime effect.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class KubernetesEntityShortNamesAttribute(params string[] shortNames) : Attribute
{
    public string[] ShortNames { get; } = shortNames;
}
