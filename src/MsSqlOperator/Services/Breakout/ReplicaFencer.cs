using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Breakout;

/// <summary>How a replica was proven unable to keep writing.</summary>
public enum FenceMethod
{
    /// <summary>Nothing proved the process is stopped — the caller must NOT promote anyone.</summary>
    NotConfirmed,

    /// <summary>The replica answered SQL and was demoted with SET (ROLE = SECONDARY): it no longer
    /// holds the primary role, so it cannot accept writes.</summary>
    Demoted,

    /// <summary>The kubelet acknowledged a graceful delete on a Ready node: containers are stopped.</summary>
    GracefulPodDeletion,

    /// <summary>Every VolumeAttachment backing the replica's PVCs is gone: the engine cannot write
    /// to its data files, whatever the process is doing on a partitioned node.</summary>
    VolumeDetached,
}

public record FenceResult(FenceMethod Method, string Detail)
{
    public bool Confirmed => Method != FenceMethod.NotConfirmed;

    public static FenceResult No(string detail) => new(FenceMethod.NotConfirmed, detail);
}

public interface IReplicaFencer
{
    /// <summary>
    /// Proves that a replica pod can no longer write to its databases, or reports that it cannot
    /// prove it. Never returns Confirmed on a guess.
    /// </summary>
    Task<FenceResult> FenceAsync(string podName, string ns, CancellationToken ct);
}

/// <summary>
/// Fencing that actually proves isolation.
///
/// The previous implementation deleted the Pod object with <c>gracePeriodSeconds=0</c> and treated
/// "the object is gone from etcd" as confirmation. On a partitioned node the API server drops the
/// object instantly while the kubelet — which is precisely the component that is unreachable —
/// never stops <c>sqlservr</c>: the old primary keeps serving writes off an attached disk, and the
/// instant disappearance of the Pod object even hides it from the breakout controller (which only
/// scans pods carrying a deletionTimestamp), disabling the one mechanism that would have detached
/// its volumes.
///
/// The protocol here is: on a Ready node, delete gracefully and let the kubelet confirm the kill.
/// On a NotReady/absent node, take the storage away — delete every VolumeAttachment backing the
/// replica's PVCs and confirm they are gone. A CSI driver that does not use VolumeAttachments
/// (<c>attachRequired: false</c> — NFS, local-path, hostPath) can prove nothing, so fencing is
/// reported as NOT confirmed rather than silently assumed.
/// </summary>
public class ReplicaFencer(
    ILogger<ReplicaFencer> logger,
    IKubernetesClient kubernetesClient,
    IFencingDriver fencingDriver,
    TimeProvider timeProvider) : IReplicaFencer
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<FenceResult> FenceAsync(string podName, string ns, CancellationToken ct)
    {
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);

        if (pod is not null && !string.IsNullOrEmpty(pod.Spec?.NodeName))
        {
            var node = await kubernetesClient.GetAsync<V1Node>(pod.Spec.NodeName, null, ct);
            var nodeReady = node is not null && FailoverBreakoutService.IsNodeReady(node);

            if (nodeReady)
            {
                // A live kubelet honours a normal delete: SIGTERM → SIGKILL → containers stopped.
                // Its acknowledgement (the object leaving etcd) is real proof of termination.
                logger.LogWarning("FENCING {Pod}: node {Node} is Ready — gracefully deleting the pod and awaiting kubelet confirmation",
                    podName, pod.Spec.NodeName);
                await kubernetesClient.DeleteAsync(pod, ct);
                if (await WaitPodGoneAsync(podName, ns, pod.Metadata.Uid, ct))
                    return new FenceResult(FenceMethod.GracefulPodDeletion,
                        $"pod {podName} gracefully deleted and confirmed gone by the kubelet on Ready node {pod.Spec.NodeName}");

                logger.LogWarning("FENCING {Pod}: graceful deletion not acknowledged within {Timeout}s; falling back to storage fencing",
                    podName, ConfirmTimeout.TotalSeconds);
            }
        }

        // Node is NotReady/absent, or the kubelet never acknowledged: the only proof left is that
        // the storage is detached.
        return await FenceByStorageAsync(pod, podName, ns, ct);
    }

    private async Task<FenceResult> FenceByStorageAsync(V1Pod? pod, string podName, string ns, CancellationToken ct)
    {
        var pvcNames = await ResolvePvcNamesAsync(pod, podName, ns, ct);
        if (pvcNames.Count == 0)
            return FenceResult.No(
                $"no PersistentVolumeClaim could be resolved for {podName}: nothing proves the SQL Server process is stopped");

        var pvNames = new List<string>();
        foreach (var pvcName in pvcNames)
        {
            var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
            if (pvc?.Spec?.VolumeName is { Length: > 0 } pv)
                pvNames.Add(pv);
        }

        if (pvNames.Count == 0)
            return FenceResult.No($"PVCs of {podName} are not bound to any PersistentVolume; fencing cannot be proven");

        // A driver that never creates VolumeAttachments cannot prove anything by their absence.
        var provable = await AttachmentsAreProofAsync(pvNames, ct);
        if (!provable.IsProof)
            return FenceResult.No(
                $"the CSI driver backing {podName} does not use VolumeAttachments ({provable.Reason}); " +
                "detachment cannot be proven — fence the node externally (or set failover.splitBrainProtection.fencingStrategy=External) " +
                "and re-issue the failover once the old primary is confirmed down");

        var attachments = await FindAttachmentsAsync(pvNames, ct);
        if (attachments.Count > 0)
        {
            var stub = pod ?? new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = podName, NamespaceProperty = ns },
                Spec = new V1PodSpec { NodeName = attachments[0].Spec.NodeName },
            };
            await fencingDriver.DetachVolumesAsync(stub, attachments, ct);

            var deadline = timeProvider.GetUtcNow() + ConfirmTimeout;
            while (timeProvider.GetUtcNow() < deadline)
            {
                if ((await FindAttachmentsAsync(pvNames, ct)).Count == 0)
                    break;
                await Task.Delay(PollInterval, timeProvider, ct);
            }

            var remaining = await FindAttachmentsAsync(pvNames, ct);
            if (remaining.Count > 0)
                return FenceResult.No(
                    $"{remaining.Count} VolumeAttachment(s) of {podName} are still present after {ConfirmTimeout.TotalSeconds}s " +
                    "(the CSI external-attacher has not run ControllerUnpublishVolume — typical on a dead node); " +
                    "the old primary may still be writing, so no promotion is safe");
        }

        // Storage is detached. Remove the pod object too so the StatefulSet can reschedule it.
        var still = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
        if (still is not null)
        {
            await kubernetesClient.ForceDeletePodAsync(podName, ns, ct);
            await WaitPodGoneAsync(podName, ns, still.Metadata.Uid, ct);
        }

        logger.LogWarning("FENCING {Pod}: all VolumeAttachments detached — the replica cannot write to its databases", podName);
        return new FenceResult(FenceMethod.VolumeDetached,
            $"all VolumeAttachments backing {podName} are detached; the replica cannot write to its data files");
    }

    /// <summary>PVCs of the pod, or — when the Pod object is already gone — the PVCs the
    /// StatefulSet's volumeClaimTemplates produce for that ordinal (<c>{template}-{pod}</c>).</summary>
    private async Task<List<string>> ResolvePvcNamesAsync(V1Pod? pod, string podName, string ns, CancellationToken ct)
    {
        var fromPod = pod?.Spec?.Volumes?
            .Where(v => v.PersistentVolumeClaim != null)
            .Select(v => v.PersistentVolumeClaim.ClaimName)
            .ToList();
        if (fromPod is { Count: > 0 })
            return fromPod;

        var all = await kubernetesClient.ListAsync<V1PersistentVolumeClaim>(ns, null, ct);
        return all
            .Where(p => p.Metadata.Name.EndsWith($"-{podName}", StringComparison.Ordinal))
            .Select(p => p.Metadata.Name)
            .ToList();
    }

    private async Task<List<V1VolumeAttachment>> FindAttachmentsAsync(List<string> pvNames, CancellationToken ct)
    {
        var all = await kubernetesClient.ListAsync<V1VolumeAttachment>(null, null, ct);
        return all
            .Where(va => va.Spec.Source?.PersistentVolumeName is { } pv && pvNames.Contains(pv))
            .ToList();
    }

    /// <summary>
    /// True when the absence of a VolumeAttachment is meaningful — i.e. every PV is CSI-backed by a
    /// driver declaring <c>attachRequired: true</c>. For NFS/local-path/hostPath volumes the
    /// external-attacher never creates an attachment, so "no attachment" says nothing about whether
    /// the old primary still has the filesystem mounted.
    /// </summary>
    private async Task<(bool IsProof, string Reason)> AttachmentsAreProofAsync(List<string> pvNames, CancellationToken ct)
    {
        foreach (var pvName in pvNames)
        {
            var pv = await kubernetesClient.GetAsync<V1PersistentVolume>(pvName, null, ct);
            if (pv is null)
                return (false, $"PersistentVolume {pvName} not found");

            var driver = pv.Spec?.Csi?.Driver;
            if (string.IsNullOrEmpty(driver))
                return (false, $"PersistentVolume {pvName} is not CSI-backed");

            var csiDriver = await kubernetesClient.GetAsync<V1CSIDriver>(driver, null, ct);
            if (csiDriver is null)
                return (false, $"CSIDriver {driver} not found");
            if (csiDriver.Spec?.AttachRequired != true)
                return (false, $"CSIDriver {driver} declares attachRequired=false");
        }
        return (true, "all volumes are CSI-attached");
    }

    private async Task<bool> WaitPodGoneAsync(string podName, string ns, string? uid, CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + ConfirmTimeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
            // The StatefulSet recreates the pod under the SAME name: only a different UID (or no
            // pod at all) proves the fenced instance is gone.
            if (pod is null || (uid is not null && pod.Metadata.Uid != uid))
                return true;
            await Task.Delay(PollInterval, timeProvider, ct);
        }
        return false;
    }
}
