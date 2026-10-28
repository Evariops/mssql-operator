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

    /// <summary>When true, the force-delete hangs until cancelled (an API call that never answers).</summary>
    public bool Hang { get; set; }

    public Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        ForceDeleteCalls.Add(pod);
        if (Hang)
            return Task.Delay(Timeout.Infinite, ct);
        return Task.CompletedTask;
    }
}
