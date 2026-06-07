namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Declares an <c>additionalPrinterColumns</c> entry for the entity's CRD, surfaced by
/// <c>kubectl get</c>. Apply one per column on the entity class. JsonPath points at a field that
/// lives in the (slim) status — these are the etcd-free replacement for scrolling large status arrays.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PrinterColumnAttribute(string name, string jsonPath, string type = "string", int priority = 0) : Attribute
{
    /// <summary>Column header shown by kubectl.</summary>
    public string Name { get; } = name;

    /// <summary>JSONPath into the resource, e.g. <c>.status.phase</c>.</summary>
    public string JsonPath { get; } = jsonPath;

    /// <summary>OpenAPI type: string | integer | number | boolean | date.</summary>
    public string Type { get; } = type;

    /// <summary>0 shows in the default table; &gt;0 only with <c>-o wide</c>.</summary>
    public int Priority { get; } = priority;
}
