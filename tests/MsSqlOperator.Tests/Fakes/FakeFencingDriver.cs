using k8s.Models;
using MsSqlOperator.Services.Breakout;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IFencingDriver"/>: records detach + force-delete calls.</summary>
public sealed class FakeFencingDriver : IFencingDriver
{
    public readonly List<(V1Pod Pod, IReadOnlyList<V1VolumeAttachment> Attachments)> DetachCalls = [];
    public readonly List<V1Pod> ForceDeleteCalls = [];

    public Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct)
    {
        DetachCalls.Add((pod, volumeAttachments));
        return Task.CompletedTask;
    }

    public Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        ForceDeleteCalls.Add(pod);
        return Task.CompletedTask;
    }
}
