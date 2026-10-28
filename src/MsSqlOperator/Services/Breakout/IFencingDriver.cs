using k8s.Models;

namespace MsSqlOperator.Services.Breakout;

public interface IFencingDriver
{
    /// <summary>True for the observation driver (fencingDriver=none): nothing may be deleted or
    /// detached, by the driver or by the replica fencer.</summary>
    bool DryRun => false;

    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DeleteVolumeAttachment)]
    Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct);
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.ForceDeletePod)]
    Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct);
}
