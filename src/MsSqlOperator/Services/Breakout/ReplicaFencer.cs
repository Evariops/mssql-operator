using MsSqlOperator.Services.Actuation;
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

    /// <summary>Fencing is under way: nothing is proven yet, call again on the next pass.</summary>
    public bool InProgress { get; init; }

    public static FenceResult No(string detail) => new(FenceMethod.NotConfirmed, detail);

    public static FenceResult Pending(string detail) => new(FenceMethod.NotConfirmed, detail) { InProgress = true };
}

public interface IReplicaFencer
{
    /// <summary>
    /// Advances the fencing of a replica pod by one bounded step, recording its progress in
    /// <paramref name="state"/> (persisted by the caller between passes). Returns Confirmed once the
    /// pod provably can no longer write, NotConfirmed when that cannot be proven, and InProgress
    /// while waiting for the kubelet or the CSI attacher. Never returns Confirmed on a guess.
    /// </summary>
    Task<FenceResult> FenceAsync(string podName, string ns, Entities.V1Alpha1.AgFencingStatus state,
        TimeSpan storageTimeout, CancellationToken ct);
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
///
/// Each call does a bounded amount of work and returns; the waits (the pod's own grace
/// period, then the detach) span passes through the persisted state. Waiting inside one pass took
/// grace + 30 s (210 s by default), past the 2-minute budget of an AG pass: the pass was cancelled,
/// the promotion never happened, and the next pass started over on the recreated pod.
/// </summary>
public class ReplicaFencer(
    ILogger<ReplicaFencer> logger,
    IKubernetesClient kubernetesClient,
    IFencingDriver fencingDriver,
    TimeProvider timeProvider,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null,
    Microsoft.Extensions.Options.IOptions<BreakoutOptions>? breakoutOptions = null) : IReplicaFencer
{
    /// <summary>The CSI drivers whose detachment withdraws a node's access (breakout.trustedFencingCsiDrivers).
    /// Empty: any driver with <c>attachRequired: true</c> counts, as before the list existed.</summary>
    private IReadOnlyCollection<string> TrustedDrivers => breakoutOptions?.Value.TrustedFencingCsiDrivers ?? [];

    /// <summary>A node Lease not renewed for this long (the kubelet renews it every 10 s) means
    /// a lost node, whatever its Ready condition, which the node controller updates 40 to 50 s
    /// after the loss only.</summary>
    internal static readonly TimeSpan NodeLeaseStaleAfter = TimeSpan.FromSeconds(40);

    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    public const string GracefulDeletionStage = "GracefulDeletion";
    public const string StorageDetachStage = "StorageDetach";

    /// <summary>Safety margin added to the pod's own grace period when waiting for a graceful
    /// deletion on a healthy node.</summary>
    private static readonly TimeSpan GracefulDeleteMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on the grace period of a fencing deletion. A frozen primary answers
    /// neither the preStop CHECKPOINT nor its SIGTERM: only the SIGKILL at the end of its own grace
    /// period (180 s by default) stopped it, and the failover waited for it. An orderly shutdown is
    /// worth little on a replica being replaced — the next startup replays its log either way — and
    /// a responsive one is down in seconds.
    /// </summary>
    internal const int FencingGracePeriodSeconds = 30;

    public async Task<FenceResult> FenceAsync(string podName, string ns, Entities.V1Alpha1.AgFencingStatus state,
        TimeSpan storageTimeout, CancellationToken ct)
    {
        using var operation = MsSqlOperator.Services.Metrics.OperatorTracing.Start(logger, "fencing", $"{ns}/{podName}", ("stage", state.Stage));
        // fencingDriver=none is an observation mode: it used to still delete the pod gracefully.
        if (fencingDriver.DryRun)
            return FenceResult.No("fencingDriver=none (observation mode): nothing is deleted or detached, so isolation cannot be proven");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);

        switch (state.Stage)
        {
            case GracefulDeletionStage:
                // The StatefulSet recreates the pod under the SAME name: only a different UID (or no
                // pod at all) proves the fenced instance is gone.
                if (pod is null || pod.Metadata.Uid != state.PodUid)
                    return new FenceResult(FenceMethod.GracefulPodDeletion,
                        $"pod {podName} gracefully deleted and confirmed gone by the kubelet");
                if (now < state.Deadline)
                    return FenceResult.Pending($"waiting for the kubelet to stop {podName} (until {state.Deadline:O})");
                logger.LogWarning("FENCING {Pod}: graceful deletion not acknowledged in time; falling back to storage fencing", podName);
                return await StartStorageFencingAsync(state, pod, podName, ns, now, storageTimeout, ct);

            case StorageDetachStage:
                return await ContinueStorageFencingAsync(state, pod, podName, ns, now, ct);
        }

        // New fencing.
        state.Pod = podName;
        state.PodUid = pod?.Metadata.Uid;
        state.StartedAt = now;

        // The fencing targets the instance that was primary, not the name. A pod of that name
        // created after the primary was lost is its replacement — the original object went without
        // any kubelet confirming the stop (forced deletion, node object deleted) — and the deletion
        // of the replacement proves nothing about the original: only the storage can.
        if (pod is not null && state.PrimaryLostAt is { } lostAt && pod.Metadata.CreationTimestamp is { } created
            && created.ToUniversalTime() > DateTime.SpecifyKind(lostAt, DateTimeKind.Utc))
        {
            logger.LogWarning("FENCING {Pod}: the pod was created at {Created:O}, after the primary was lost at {Lost:O}: it is a " +
                              "replacement, its deletion would prove nothing — fencing the storage", podName, created.ToUniversalTime(), lostAt);
            return await StartStorageFencingAsync(state, pod, podName, ns, now, storageTimeout, ct);
        }

        if (pod is not null && !string.IsNullOrEmpty(pod.Spec?.NodeName))
        {
            var node = await kubernetesClient.GetAsync<V1Node>(pod.Spec.NodeName, null, ct);
            if (node is not null && FailoverBreakoutService.IsNodeReady(node) && !await NodeLeaseStaleAsync(pod.Spec.NodeName, now, ct))
            {
                // A live kubelet honours a normal delete: SIGTERM → SIGKILL → containers stopped. Its
                // acknowledgement (the object leaving etcd) is real proof of termination.
                // the pod's OWN grace period is awaited (preStop CHECKPOINT + orderly shutdown).
                var graceSeconds = (int)Math.Min(pod.Spec.TerminationGracePeriodSeconds ?? 30, FencingGracePeriodSeconds);
                var grace = TimeSpan.FromSeconds(graceSeconds) + GracefulDeleteMargin;
                logger.LogWarning("FENCING {Pod}: node {Node} is Ready — gracefully deleting the pod with a {Grace}s grace period (kubelet confirmation awaited up to {Timeout}s)",
                    podName, pod.Spec.NodeName, graceSeconds, grace.TotalSeconds);
                await Gateway.DeletePodAsync(pod, kubernetesClient, graceSeconds, ct);
                state.Stage = GracefulDeletionStage;
                state.Deadline = now + grace;
                return FenceResult.Pending($"graceful deletion of {podName} requested on Ready node {pod.Spec.NodeName}");
            }
        }

        // Node is NotReady/absent: the only proof left is that the storage is detached.
        return await StartStorageFencingAsync(state, pod, podName, ns, now, storageTimeout, ct);
    }

    /// <summary>True when the node's Lease has not been renewed for <see cref="NodeLeaseStaleAfter"/>.
    /// Unknown (no Lease, no permission) is false: the Ready condition decides, as before.</summary>
    private async Task<bool> NodeLeaseStaleAsync(string nodeName, DateTime now, CancellationToken ct)
    {
        try
        {
            var lease = await kubernetesClient.GetAsync<V1Lease>(nodeName, "kube-node-lease", ct);
            if (lease?.Spec?.RenewTime is not { } renewed || now - renewed.ToUniversalTime() < NodeLeaseStaleAfter)
                return false;
            logger.LogWarning("FENCING: node {Node} is still Ready but its Lease was last renewed at {Renewed:O}: it is lost, the " +
                              "graceful deletion would never be confirmed — fencing the storage", nodeName, renewed.ToUniversalTime());
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read the Lease of node {Node}", nodeName);
            return false;
        }
    }

    private async Task<FenceResult> StartStorageFencingAsync(Entities.V1Alpha1.AgFencingStatus state, V1Pod? pod,
        string podName, string ns, DateTime now, TimeSpan storageTimeout, CancellationToken ct)
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

        // The pod object may be gone already (the attachment outlives it): the request still names it.
        var owner = pod ?? new V1Pod { Metadata = new V1ObjectMeta { Name = podName, NamespaceProperty = ns } };
        foreach (var attachment in await FindAttachmentsAsync(pvNames, ct))
        {
            logger.LogWarning("FENCING {Pod}: deleting VolumeAttachment {Attachment} on node {Node}",
                podName, attachment.Metadata.Name, attachment.Spec.NodeName);
            await Gateway.DeleteVolumeAttachmentAsync(owner, kubernetesClient, attachment, ct);
        }

        state.Stage = StorageDetachStage;
        state.Volumes = pvNames;
        state.Deadline = now + storageTimeout;
        return await ContinueStorageFencingAsync(state, pod, podName, ns, now, ct);
    }

    private async Task<FenceResult> ContinueStorageFencingAsync(Entities.V1Alpha1.AgFencingStatus state, V1Pod? pod,
        string podName, string ns, DateTime now, CancellationToken ct)
    {
        var remaining = await FindAttachmentsAsync(state.Volumes ?? [], ct);
        if (remaining.Count > 0)
        {
            if (now < state.Deadline)
                return FenceResult.Pending($"waiting for {remaining.Count} VolumeAttachment(s) of {podName} to be released");
            return FenceResult.No(
                $"{remaining.Count} VolumeAttachment(s) of {podName} are still present after the fencing timeout " +
                "(the CSI external-attacher has not run ControllerUnpublishVolume — typical on a dead node); " +
                "the old primary may still be writing, so no promotion is safe");
        }

        // Storage is detached. Remove the pod object too so the StatefulSet can reschedule it — only
        // the instance this fencing targets, never a recreated one.
        if (pod is not null && pod.Metadata.Uid == state.PodUid)
            await Gateway.ForceDeletePodAsync(pod, kubernetesClient, ct);

        logger.LogWarning("FENCING {Pod}: all VolumeAttachments detached — the replica cannot write to its databases", podName);
        return new FenceResult(FenceMethod.VolumeDetached,
            $"all VolumeAttachments backing {podName} are detached; the replica cannot write to its data files");
    }

    /// <summary>PVCs of the pod, or — when the Pod object is already gone — the PVCs the
    /// StatefulSet's volumeClaimTemplates produce for that ordinal (<c>{template}-{pod}</c>).
    ///
    /// The fallback used to suffix-match every PVC of the namespace with
    /// <c>EndsWith("-{podName}")</c>, which also matched the PVCs of ANOTHER StatefulSet whose pod
    /// names end identically ("data-other-ag-1" for pod "ag-1") — fencing then detached the volumes
    /// of an unrelated, healthy instance. The fenced pod is operator-managed, so its PVC names are
    /// exactly <c>{template}-{podName}</c> over the operator's fixed template set.</summary>
    private async Task<List<string>> ResolvePvcNamesAsync(V1Pod? pod, string podName, string ns, CancellationToken ct)
    {
        var fromPod = pod?.Spec?.Volumes?
            .Where(v => v.PersistentVolumeClaim != null)
            .Select(v => v.PersistentVolumeClaim.ClaimName)
            .ToList();
        if (fromPod is { Count: > 0 })
            return fromPod;

        var names = new List<string>();
        foreach (var candidate in CandidatePvcNames(podName))
        {
            if (await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(candidate, ns, ct) is not null)
                names.Add(candidate);
        }
        return names;
    }

    /// <summary>The exact PVC names an operator-built StatefulSet can have produced for this pod.</summary>
    internal static IEnumerable<string> CandidatePvcNames(string podName)
        => Services.Kubernetes.StatefulSetBuilder.VolumeClaimTemplateNames.Select(t => $"{t}-{podName}");

    private async Task<List<V1VolumeAttachment>> FindAttachmentsAsync(List<string> pvNames, CancellationToken ct)
    {
        if (pvNames.Count == 0)
            return [];
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
            // A VolumeAttachment can be gone while the node keeps its access (a driver whose
            // ControllerUnpublishVolume does nothing on the storage side). The breakout trusted only the
            // drivers of its list; the failover fencing trusted every attaching driver.
            var trusted = TrustedDrivers;
            if (trusted.Count > 0 && !trusted.Contains(driver, StringComparer.Ordinal))
                return (false, $"CSI driver {driver} is not in breakout.trustedFencingCsiDrivers");
        }
        return (true, "all volumes are CSI-attached");
    }
}
