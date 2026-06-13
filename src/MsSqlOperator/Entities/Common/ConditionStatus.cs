using System.ComponentModel.DataAnnotations;

namespace MsSqlOperator.Entities.Common;

/// <summary>
/// metav1.Condition-conformant condition. The codegen detects any
/// <c>List&lt;ConditionStatus&gt;</c> property and marks it <c>x-kubernetes-list-type: map</c>
/// keyed by <c>type</c>, so a server-side apply merges (not replaces) the array.
/// </summary>
public class ConditionStatus
{
    [MaxLength(316)]
    public string Type { get; set; } = string.Empty;

    /// <summary>True | False | Unknown (metav1.ConditionStatus).</summary>
    public string Status { get; set; } = "False";

    /// <summary>PascalCase machine-readable reason (required by metav1.Condition).</summary>
    [MaxLength(1024)]
    public string Reason { get; set; } = "Unknown";

    [MaxLength(32768)]
    public string? Message { get; set; }

    /// <summary>The .metadata.generation the condition was computed against (metav1.Condition).</summary>
    public long? ObservedGeneration { get; set; }

    public DateTime LastTransitionTime { get; set; } = DateTime.UtcNow;
}
