using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Breakout;

public class VolumeAttachmentFencingDriver(
    ILogger<VolumeAttachmentFencingDriver> logger,
    IKubernetesClient kubernetesClient) : IFencingDriver
{
    public async Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;

        foreach (var va in volumeAttachments)
        {
            var vaName = va.Metadata.Name;
            logger.LogWarning("Deleting VolumeAttachment {VaName} for pod {PodName} on node {Node}",
                vaName, podName, va.Spec.NodeName);

            await kubernetesClient.DeleteAsync(va, ct);

            logger.LogInformation("VolumeAttachment {VaName} deleted successfully", vaName);
        }
    }

    public async Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;
        var ns = pod.Metadata.NamespaceProperty;

        logger.LogWarning("Force-deleting pod {PodName} in namespace {Namespace} with gracePeriod=0", podName, ns);

        // Use grace period 0 to force immediate deletion of stuck Terminating pods
        pod.Metadata.DeletionGracePeriodSeconds = 0;
        await kubernetesClient.DeleteAsync(pod, ct);

        logger.LogInformation("Pod {PodName} force-deleted", podName);
    }
}
