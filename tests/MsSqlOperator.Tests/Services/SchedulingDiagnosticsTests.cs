using k8s.Models;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class SchedulingDiagnosticsTests
{
    private static V1Pod Pod(string phase, params (string type, string status, string? reason, string? msg)[] conditions) => new()
    {
        Status = new V1PodStatus
        {
            Phase = phase,
            Conditions = [.. conditions.Select(c => new V1PodCondition { Type = c.type, Status = c.status, Reason = c.reason, Message = c.msg })]
        }
    };

    [Fact]
    public void NullPod_ReturnsNull() => Assert.Null(SchedulingDiagnostics.UnschedulableReason(null));

    [Fact]
    public void RunningPod_ReturnsNull() =>
        Assert.Null(SchedulingDiagnostics.UnschedulableReason(Pod("Running", ("Ready", "True", null, null))));

    [Fact]
    public void PendingUnschedulable_ReturnsSchedulerMessage()
    {
        var pod = Pod("Pending", ("PodScheduled", "False", "Unschedulable",
            "0/3 nodes are available: 3 didn't match pod topology spread constraints."));

        Assert.Contains("topology spread constraints", SchedulingDiagnostics.UnschedulableReason(pod));
    }

    [Fact]
    public void PendingButScheduling_NotYetUnschedulable_ReturnsNull()
    {
        // PodScheduled missing/True while images pull etc. — a normal transient wait, not blocked.
        var pod = Pod("Pending", ("PodScheduled", "True", null, null));

        Assert.Null(SchedulingDiagnostics.UnschedulableReason(pod));
    }

    [Fact]
    public void PendingUnschedulable_NoMessage_FallsBackToGenericReason()
    {
        var pod = Pod("Pending", ("PodScheduled", "False", "Unschedulable", null));

        Assert.Equal("unschedulable", SchedulingDiagnostics.UnschedulableReason(pod));
    }
}
