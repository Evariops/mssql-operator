using k8s.Models;

namespace MsSqlOperator.Services.Breakout;

public interface IFencingDriver
{
    /// <summary>True for the observation driver (fencingDriver=none): nothing may be deleted or
    /// detached, by the driver or by the replica fencer.</summary>
    bool DryRun => false;

    Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct);
    Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct);
}
