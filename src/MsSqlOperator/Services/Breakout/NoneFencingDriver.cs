using k8s.Models;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Breakout;

public class NoneFencingDriver(ILogger<NoneFencingDriver> logger) : IFencingDriver
{
    public bool DryRun => true;

    public Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct)
    {
        foreach (var va in volumeAttachments)
        {
            logger.LogInformation("[DRY-RUN] Would delete VolumeAttachment {VaName} for pod {PodName} on node {Node}",
                va.Metadata.Name, pod.Metadata.Name, va.Spec.NodeName);
        }

        return Task.CompletedTask;
    }

    public Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        logger.LogInformation("[DRY-RUN] Would force-delete pod {PodName} in namespace {Namespace}",
            pod.Metadata.Name, pod.Metadata.NamespaceProperty);

        return Task.CompletedTask;
    }
}
