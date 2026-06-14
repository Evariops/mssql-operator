using k8s.Models;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Reads the scheduler's verdict off a pod. A pure function so it is unit-testable
/// without the API server.
/// </summary>
public static class SchedulingDiagnostics
{
    /// <summary>
    /// Returns the scheduler's message when a pod is Pending and cannot be scheduled
    /// (<c>PodScheduled=False</c>, reason <c>Unschedulable</c>) — e.g. an unsatisfiable
    /// <c>DoNotSchedule</c> topology spread. Returns null otherwise (running, scheduled, gone, or
    /// pending for an unrelated reason).
    /// </summary>
    public static string? UnschedulableReason(V1Pod? pod)
    {
        // Only a Pending pod can be unschedulable; a stale condition on a Running pod is irrelevant.
        if (!string.Equals(pod?.Status?.Phase, "Pending", StringComparison.OrdinalIgnoreCase))
            return null;

        var scheduled = pod!.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled");
        if (scheduled is { Status: "False", Reason: "Unschedulable" })
            return string.IsNullOrWhiteSpace(scheduled.Message) ? "unschedulable" : scheduled.Message;

        return null;
    }
}
