using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Shared condition handling. Following the Kubernetes convention, <c>lastTransitionTime</c> changes
/// only when the condition's <c>Status</c> actually transitions — not on every reconcile. This keeps
/// the status byte-stable across no-op reconciles, which is what lets the status-write dedup
/// (see <c>KubernetesClientAdapter.UpdateStatusAsync</c>) skip redundant writes and break the
/// self-reconcile feedback loop.
/// </summary>
public static class ConditionOps
{
    public static void Set(List<ConditionStatus> conditions, string type, string status, string? reason, string? message, long? observedGeneration = null)
    {
        // metav1.Condition requires a PascalCase machine-readable reason.
        var effectiveReason = string.IsNullOrEmpty(reason) ? "Unknown" : reason;

        var existing = conditions.FirstOrDefault(c => c.Type == type);
        if (existing is null)
        {
            conditions.Add(new ConditionStatus
            {
                Type = type,
                Status = status,
                Reason = effectiveReason,
                Message = message,
                ObservedGeneration = observedGeneration,
                LastTransitionTime = DateTime.UtcNow,
            });
            return;
        }

        if (existing.Status != status)
            existing.LastTransitionTime = DateTime.UtcNow;
        existing.Status = status;
        existing.Reason = effectiveReason;
        existing.Message = message;
        if (observedGeneration is not null)
            existing.ObservedGeneration = observedGeneration;
    }
}
