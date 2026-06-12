using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Breakout;

/// <summary>Raised when a fencing action could not be confirmed: the caller must
/// treat the replica as NOT fenced — never proceed to a data-loss failover on uncertainty.</summary>
public class FencingNotConfirmedException(string message) : InvalidOperationException(message);

public class VolumeAttachmentFencingDriver(
    ILogger<VolumeAttachmentFencingDriver> logger,
    IKubernetesClient kubernetesClient,
    TimeProvider timeProvider) : IFencingDriver
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task DetachVolumesAsync(V1Pod pod, IReadOnlyList<V1VolumeAttachment> volumeAttachments, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;

        foreach (var va in volumeAttachments)
        {
            var vaName = va.Metadata.Name;
            logger.LogWarning("Deleting VolumeAttachment {VaName} for pod {PodName} on node {Node}",
                vaName, podName, va.Spec.NodeName);

            await kubernetesClient.DeleteAsync(va, ct);
        }

        // The delete only marks the object; ControllerUnpublishVolume has not run
        // until the external-attacher releases its finalizer and the object disappears. Wait
        // bounded here; the breakout loop re-verifies on the next pass anyway and defers the
        // pod deletion while any attachment is still visible.
        foreach (var va in volumeAttachments)
        {
            if (!await WaitGoneAsync<V1VolumeAttachment>(va.Metadata.Name, null, ct))
                logger.LogWarning(
                    "VolumeAttachment {VaName} still present after {Timeout}s (finalizer not released — " +
                    "ControllerUnpublishVolume pending). Pod deletion stays deferred. On a dead node some " +
                    "CSI drivers never confirm; external/hardware fencing is required for those.",
                    va.Metadata.Name, ConfirmTimeout.TotalSeconds);
            else
                logger.LogInformation("VolumeAttachment {VaName} fully detached", va.Metadata.Name);
        }
    }

    public async Task ForceDeletePodAsync(V1Pod pod, CancellationToken ct)
    {
        var podName = pod.Metadata.Name;
        var ns = pod.Metadata.NamespaceProperty;

        logger.LogWarning("Force-deleting pod {PodName} in namespace {Namespace} with gracePeriod=0", podName, ns);
        await kubernetesClient.ForceDeletePodAsync(podName, ns, ct);

        // Fencing is only acquired when the pod is actually gone from etcd.
        if (!await WaitGoneAsync<V1Pod>(podName, ns, ct))
            throw new FencingNotConfirmedException(
                $"Pod {ns}/{podName} still present {ConfirmTimeout.TotalSeconds}s after force deletion; fencing not confirmed.");

        logger.LogInformation("Pod {PodName} force-deleted (confirmed gone)", podName);
    }

    private async Task<bool> WaitGoneAsync<T>(string name, string? ns, CancellationToken ct)
        where T : class, IKubernetesObject<V1ObjectMeta>
    {
        var deadline = timeProvider.GetUtcNow() + ConfirmTimeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            if (await kubernetesClient.GetAsync<T>(name, ns, ct) is null)
                return true;
            await Task.Delay(PollInterval, timeProvider, ct);
        }
        return await kubernetesClient.GetAsync<T>(name, ns, ct) is null;
    }
}
