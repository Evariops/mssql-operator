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

    /// <summary>
    /// Grows a replica's backup volume after a write to it failed: no database file lives
    /// there, so SQL Server cannot report its free space beforehand. Says what was done, or why not.
    /// </summary>
    Task<string> GrowBackupVolumeAsync(SqlAvailabilityGroup ag, string replica, CancellationToken ct);
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

    /// <summary>A volume grown (<see cref="Grown"/>), or one that could not be (<see cref="Blocked"/>).</summary>
    private sealed record Growth(string? Grown, string? Blocked);

    private static GrowableVolume[] Volumes(SqlAvailabilityGroup ag, AgVolumeAutoGrowSpec policy)
    {
        var storage = ag.Spec.InstanceTemplate?.Storage;
        return
        [
            new("data", StatefulSetBuilder.DataMountPath, storage?.Data ?? StatefulSetBuilder.DefaultDataVolume, policy.DataMaxSize),
            new("log", StatefulSetBuilder.LogMountPath, storage?.Log ?? StatefulSetBuilder.DefaultLogVolume, policy.LogMaxSize),
            new("backup", StatefulSetBuilder.BackupMountPath, storage?.Backup ?? StatefulSetBuilder.DefaultBackupVolume, policy.BackupMaxSize),
        ];
    }

    public async Task<string?> ReconcileAsync(SqlAvailabilityGroup ag, IEnumerable<string> replicas, string saPassword, CancellationToken ct)
    {
        var policy = ag.Spec.VolumeAutoGrow ?? new AgVolumeAutoGrowSpec();
        if (policy.Mode != "Auto")
            return null;

        var name = ag.Metadata.Name;
        var ns = ag.Metadata.NamespaceProperty;
        var volumes = Volumes(ag, policy);

        var grown = new List<string>();
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
                    var growth = await GrowAsync(ag, replica, volume, $"{usedPercent:0}% full", policy.GrowPercent, ct);
                    if (growth.Grown is { } g)
                        grown.Add(g);
                    if (growth.Blocked is { } b)
                        blocked.Add(b);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "AG {Ag}: could not grow the {Volume} volume of {Replica}", name, volume.Template, replica);
                }
            }
        }

        // One event per pass: events of one reason replace each other when their message changes.
        if (grown.Count > 0)
            await eventPublisher.PublishAsync(ag, "VolumeExpanded", $"Grown online: {string.Join("; ", grown)}.", ct: ct);
        return blocked.Count == 0 ? null : string.Join(" ", blocked);
    }

    public async Task<string> GrowBackupVolumeAsync(SqlAvailabilityGroup ag, string replica, CancellationToken ct)
    {
        var policy = ag.Spec.VolumeAutoGrow ?? new AgVolumeAutoGrowSpec();
        if (policy.Mode != "Auto")
            return "volumeAutoGrow.mode is Disabled: grow it by hand, or free space";
        var growth = await GrowAsync(ag, replica, Volumes(ag, policy).Single(v => v.Template == "backup"),
            "full (a write to it failed)", policy.GrowPercent, ct);
        if (growth.Grown is { } grown)
        {
            await eventPublisher.PublishAsync(ag, "VolumeExpanded", $"Grown online: {grown}.", ct: ct);
            return $"its volume was grown ({grown})";
        }
        return growth.Blocked ?? "its volume is still being grown";
    }

    private async Task<Growth> GrowAsync(SqlAvailabilityGroup ag, string replica, GrowableVolume volume, string why,
        int growPercent, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var pvcName = $"{volume.Template}-{replica}";
        var pvc = await kubernetesClient.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
        if (pvc?.Spec?.Resources?.Requests is not { } requests || !requests.TryGetValue("storage", out var requested))
            return new Growth(null, null);

        // The filesystem has not caught up with the last request yet: growing again would stack.
        var requestedBytes = Bytes(requested);
        if (pvc.Status?.Capacity is not { } capacity || !capacity.TryGetValue("storage", out var actual)
            || Bytes(actual) < requestedBytes)
            return new Growth(null, null);

        var ceiling = volume.MaxSize is { } max
            ? new ResourceQuantity(max)
            : Quantity(Bytes(new ResourceQuantity(volume.Declared.Size)) * DefaultCeilingFactor);
        var setting = $"volumeAutoGrow.{volume.Template}MaxSize";
        if (requestedBytes >= Bytes(ceiling))
            return new Growth(null, $"The {volume.Template} volume of {replica} is {why} and already at its ceiling " +
                                    $"({requested}): raise {setting}, or free space.");

        if (pvc.Spec.StorageClassName is { Length: > 0 } scName
            && await kubernetesClient.GetAsync<V1StorageClass>(scName, null, ct) is { AllowVolumeExpansion: not true })
            return new Growth(null, $"The {volume.Template} volume of {replica} is {why} but its StorageClass {scName} " +
                                    "does not allow volume expansion: grow it by hand, or free space.");

        var grownBytes = Math.Max(requestedBytes * (100 + growPercent) / 100, requestedBytes + Gi);
        var target = grownBytes >= Bytes(ceiling) ? ceiling : Quantity((grownBytes + Gi - 1) / Gi * Gi);
        await kubernetesClient.PatchAsync(pvc, [JsonPatch.Replace("/spec/resources/requests/storage", target.ToString())], ct);
        requests["storage"] = target;

        logger.LogWarning("AG {Ag}: {Pvc} was {Why}, grown from {From} to {To}", ag.Metadata.Name, pvcName, why, requested, target);
        return new Growth($"{pvcName} from {requested} to {target} ({why}; ceiling {ceiling}, {setting})", null);
    }

    private static long Bytes(ResourceQuantity quantity) => (long)quantity.ToDecimal();

    private static ResourceQuantity Quantity(long bytes) => new(bytes % Gi == 0 ? $"{bytes / Gi}Gi" : $"{bytes}");
}
