using FluentAssertions;
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
    public void NullPod_ReturnsNull() => SchedulingDiagnostics.UnschedulableReason(null).Should().BeNull();

    [Fact]
    public void RunningPod_ReturnsNull() =>
        SchedulingDiagnostics.UnschedulableReason(Pod("Running", ("Ready", "True", null, null))).Should().BeNull();

    [Fact]
    public void PendingUnschedulable_ReturnsSchedulerMessage()
    {
        var pod = Pod("Pending", ("PodScheduled", "False", "Unschedulable",
            "0/3 nodes are available: 3 didn't match pod topology spread constraints."));

        SchedulingDiagnostics.UnschedulableReason(pod).Should().Contain("topology spread constraints");
    }

    [Fact]
    public void PendingButScheduling_NotYetUnschedulable_ReturnsNull()
    {
        // PodScheduled missing/True while images pull etc. — a normal transient wait, not blocked.
        var pod = Pod("Pending", ("PodScheduled", "True", null, null));

        SchedulingDiagnostics.UnschedulableReason(pod).Should().BeNull();
    }

    [Fact]
    public void PendingUnschedulable_NoMessage_FallsBackToGenericReason()
    {
        var pod = Pod("Pending", ("PodScheduled", "False", "Unschedulable", null));

        SchedulingDiagnostics.UnschedulableReason(pod).Should().Be("unschedulable");
    }
}
