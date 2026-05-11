using k8s.Models;

namespace MsSqlOperator.Services.Breakout;

public interface IFencingDriver
{
    Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct);
    Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct);
}
