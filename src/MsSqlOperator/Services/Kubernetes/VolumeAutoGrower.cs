using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using ResourceQuantity = k8s.Models.ResourceQuantity;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Kubernetes;

public interface IVolumeAutoGrower
{
    /// <summary>
    /// Grows the data and log volumes of the given replicas that are filling up. Returns why a volume
    /// that needs to grow cannot (at its ceiling, StorageClass without expansion), or null.
    /// </summary>
    Task<string?> ReconcileAsync(SqlAvailabilityGroup ag, IEnumerable<string> replicas, string saPassword, CancellationToken ct);
}

/// <summary>
/// The log file grows when nothing truncates it fast enough — a burst, a
/// lagging secondary — and once its volume was full it could not grow any more: every write failed
/// with 9002, and the 4 GB log volume of the test AG stayed full for good. The volumes the
/// databases live on are watched through SQL Server itself (sys.dm_os_volume_stats, per file) and
/// grown online when they are <c>thresholdPercent</c> full, by <c>growPercent</c>, up to a ceiling
/// (four times the declared size by default). Only the PVC is grown: the StatefulSet template keeps
/// the declared size, which a replacement volume starts from.
/// </summary>
public class VolumeAutoGrower(
    ILogger<VolumeAutoGrower> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IEventPublisher eventPublisher) : IVolumeAutoGrower
{
    private const long Gi = 1L << 30;

    /// <summary>Default ceiling, as a multiple of the declared size.</summary>
    internal const int DefaultCeilingFactor = 4;

    private sealed record GrowableVolume(string Template, string MountPath, StorageVolumeSpec Declared, string? MaxSize);

    public async Task<string?> ReconcileAsync(SqlAvailabilityGroup ag, IEnumerable<string> replicas, string saPassword, CancellationToken ct)
    {
        var policy = ag.Spec.VolumeAutoGrow ?? new AgVolumeAutoGrowSpec();
        if (policy.Mode != "Auto")
            return null;

        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var storage = ag.Spec.InstanceTemplate?.Storage;
        GrowableVolume[] volumes =
        [
            new("data", StatefulSetBuilder.DataMountPath, storage?.Data ?? StatefulSetBuilder.DefaultDataVolume, policy.DataMaxSize),
            new("log", StatefulSetBuilder.LogMountPath, storage?.Log ?? StatefulSetBuilder.DefaultLogVolume, policy.LogMaxSize),
        ];

        var blocked = new List<string>();
        foreach (var replica in replicas)
        {
            List<FileVolumeInfo> files;
            try
            {
                await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                    $"{replica}.{name}-headless.{ns}.svc.cluster.local", SqlEndpoints.ContainerPort, saPassword, "master", ct);
                files = await agManager.GetFileVolumesAsync(conn, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "AG {Ag}: could not read the volume usage of {Replica}", name, replica);
                continue;
            }

            foreach (var volume in volumes)
            {
                // Every file of a volume reports that volume's totals.
                var onVolume = files.Where(f => f.PhysicalName.StartsWith(volume.MountPath + "/", StringComparison.Ordinal)).ToList();
                if (onVolume.Count == 0 || onVolume[0].TotalBytes <= 0)
                    continue;
                var total = onVolume[0].TotalBytes;
                var usedPercent = 100.0 * (total - onVolume.Min(f => f.AvailableBytes)) / total;
                if (usedPercent < policy.ThresholdPercent)
                    continue;

                try
                {
                    if (await GrowAsync(ag, replica, volume, usedPercent, policy.GrowPercent, ct) is { } reason)
                        blocked.Add(reason);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "AG {Ag}: could not grow the {Volume} volume of {Replica}", name, volume.Template, replica);
                }
            }
        }
        return blocked.Count == 0 ? null : string.Join(" ", blocked);
    }

    private async Task<string?> GrowAsync(SqlAvailabilityGroup ag, string replica, GrowableVolume volume, double usedPercent,
        int growPercent, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var pvcName = $"{volume.Template}-{replica}";
        var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
        if (pvc?.Spec?.Resources?.Requests is not { } requests || !requests.TryGetValue("storage", out var requested))
            return null;

        // The filesystem has not caught up with the last request yet: growing again would stack.
        var requestedBytes = Bytes(requested);
        if (pvc.Status?.Capacity is not { } capacity || !capacity.TryGetValue("storage", out var actual)
            || Bytes(actual) < requestedBytes)
            return null;

        var ceiling = volume.MaxSize is { } max
            ? new ResourceQuantity(max)
            : new ResourceQuantity($"{Bytes(new ResourceQuantity(volume.Declared.Size)) * DefaultCeilingFactor}");
        var setting = volume.Template == "data" ? "dataMaxSize" : "logMaxSize";
        if (requestedBytes >= Bytes(ceiling))
            return $"The {volume.Template} volume of {replica} is {usedPercent:0}% full and already at its ceiling " +
                   $"({requested}): raise volumeAutoGrow.{setting}, or free space.";

        if (pvc.Spec.StorageClassName is { Length: > 0 } scName
            && await kubernetesClient.GetAsync<V1StorageClass>(scName, null, ct) is { AllowVolumeExpansion: not true })
            return $"The {volume.Template} volume of {replica} is {usedPercent:0}% full but its StorageClass {scName} " +
                   "does not allow volume expansion: grow it by hand, or free space.";

        var grownBytes = Math.Max(requestedBytes * (100 + growPercent) / 100, requestedBytes + Gi);
        var target = grownBytes >= Bytes(ceiling)
            ? ceiling
            : new ResourceQuantity($"{(grownBytes + Gi - 1) / Gi}Gi");
        requests["storage"] = target;
        await kubernetesClient.UpdateAsync(pvc, ct);

        await eventPublisher.PublishAsync(ag, "VolumeExpanded",
            $"The {volume.Template} volume of {replica} was {usedPercent:0}% full: {pvcName} grown from {requested} to {target} " +
            $"(ceiling {ceiling}, volumeAutoGrow.{setting}).", ct: ct);
        logger.LogWarning("AG {Ag}: {Pvc} was {Used:0}% full, grown from {From} to {To}",
            ag.Metadata.Name, pvcName, usedPercent, requested, target);
        return null;
    }

    private static long Bytes(ResourceQuantity quantity) => (long)quantity.ToDecimal();
}
