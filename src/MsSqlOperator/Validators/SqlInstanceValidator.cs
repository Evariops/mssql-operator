using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlInstance))]
public class SqlInstanceValidator(MsSqlOperator.Operator.Client.IKubernetesClient? kubernetesClient = null)
    : ValidationWebhook<SqlInstance>
{
    public override ValidationResult Create(SqlInstance entity, bool dryRun) => Validate(entity);

    public override async Task<ValidationResult> CreateAsync(SqlInstance entity, bool dryRun, CancellationToken ct)
    {
        var result = Validate(entity);
        if (!result.Valid) return result;

        // A SqlAvailabilityGroup of the same name (or a name colliding with one of
        // its derived Service names) would share this instance's StatefulSet, ConfigMap, PVCs and key
        // Secrets — the reconcilers would then destroy each other's resources.
        if (kubernetesClient is not null
            && await CrossKindNameGuard.ConflictAsync(kubernetesClient, "SqlInstance",
                entity.Metadata.Name, entity.Metadata.NamespaceProperty, ct) is { } conflict)
            return Fail(conflict, 422);

        return result;
    }

    public override ValidationResult Update(SqlInstance oldEntity, SqlInstance entity, bool dryRun)
    {
        var result = Validate(entity);
        if (!result.Valid) return result;

        // Reject edition changes
        if (!string.Equals(oldEntity.Spec.Edition, entity.Spec.Edition, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Edition change is not supported in-place. Create a new instance instead.", 422);
        }

        // The build the instance runs — major version AND cumulative update — may only move forward.
        // spec.version alone was never the whole story: imageTag/cumulativeUpdate decide what the
        // engine actually is, so the guard compares the resolved builds, not the declared versions.
        if (BuildDowngrade(oldEntity, entity) is { } downgradeErr)
            return Fail(downgradeErr, 422);

        // Strict immutability for fields without a safe
        // orchestration; storage GROWTH is orchestrated (PVC patch + STS recreate), shrink is not.
        if (!string.Equals(oldEntity.Spec.Mode, entity.Spec.Mode, StringComparison.Ordinal))
            return Fail("spec.mode is immutable: converting between SingleNode and AvailabilityGroupMember is not an in-place operation.", 422);

        if (ValidateStorageMutation(oldEntity.Spec.Storage, entity.Spec.Storage) is { } storageErr)
            return storageErr;

        // Preserve any admission warnings produced by Validate (e.g. topology spread guidance).
        return result;
    }

    /// <summary>The rejection message when the update moves the engine build backwards, or null.
    /// The <c>allow-version-downgrade</c> annotation on the new object waives it — a rollback is a
    /// restore, and the annotation is where the author says they have one.</summary>
    private static string? BuildDowngrade(SqlInstance oldEntity, SqlInstance entity)
    {
        if (SqlServerImages.IsDowngradeApproved(entity.Metadata?.Annotations))
            return null;

        if (!SqlServerImages.TryResolve(oldEntity.Spec.ImageSelection(), out var from)
            || !SqlServerImages.TryResolve(entity.Spec.ImageSelection(), out var to))
            return null;

        return SqlServerImages.CheckUpgrade(from, to);
    }

    private ValidationResult? ValidateStorageMutation(StorageSpec? oldStorage, StorageSpec? newStorage)
    {
        if (oldStorage is null || newStorage is null) return null;

        foreach (var (name, oldVol, newVol) in new (string, StorageVolumeSpec?, StorageVolumeSpec?)[]
        {
            ("data", oldStorage.Data, newStorage.Data),
            ("log", oldStorage.Log, newStorage.Log),
            ("backup", oldStorage.Backup, newStorage.Backup),
            ("sysDb", oldStorage.SysDb, newStorage.SysDb),
            ("tempDb", oldStorage.TempDb, newStorage.TempDb),
            ("errorLogs", oldStorage.ErrorLogs, newStorage.ErrorLogs),
            ("certBackups", oldStorage.CertBackups, newStorage.CertBackups),
        })
        {
            if (oldVol is null || newVol is null) continue;

            if (!string.Equals(oldVol.StorageClassName, newVol.StorageClassName, StringComparison.Ordinal))
                return Fail($"storage.{name}.storageClassName is immutable: changing the storage class requires a data migration.", 422);

            if (oldVol.Size != newVol.Size
                && MemoryCalculator.ParseToMb(newVol.Size) < MemoryCalculator.ParseToMb(oldVol.Size))
                return Fail($"storage.{name}.size cannot shrink (Kubernetes cannot reduce a PVC); growth is orchestrated automatically.", 422);
        }

        return null;
    }

    internal ValidationResult Validate(SqlInstance entity)
    {
        // Refuse BestEffort QoS explicitly. A database without
        // memory bounds is evicted first, OOM-killed under pressure, and SQL Server sizes
        // itself to ~80% of the NODE's RAM. No injected default — force a conscious choice.
        if (entity.Spec.Memory == null && entity.Spec.Resources == null)
            return Fail("spec.memory or spec.resources must be set: a BestEffort SQL Server pod is the first evicted under node pressure and sizes itself to the node's RAM.", 422);

        var warnings = new List<string>();

        // Version, edition, and how the build is pinned (cumulativeUpdate,
        // baseOs, imageTag, image) are one decision, not five — the image is what actually runs, so
        // the image is what gets checked.
        if (SqlServerImages.CheckSelection(entity.Spec.ImageSelection(), warnings) is { } selectionErr)
            return Fail($"spec: {selectionErr}", 422);

        if (entity.Spec.Mode != "SingleNode" && entity.Spec.Mode != "AvailabilityGroupMember")
        {
            return Fail("Mode must be 'SingleNode' or 'AvailabilityGroupMember'", 422);
        }

        // Memory validation
        if (entity.Spec.Memory != null)
        {
            var (valid, message) = MemorySpecValidator.Validate(entity.Spec.Memory);
            if (!valid) return Fail(message!, 422);
        }

        // CpuPinning validation
        var cpuPinningResult = ValidateCpuPinning(entity.Spec);
        if (!cpuPinningResult.Valid) return cpuPinningResult;

        // Kata without CPU limits: SQL Server will see all VM vCPUs (licensing concern)
        if (MemoryCalculator.IsKataRuntime(entity.Spec.RuntimeClassName) &&
            entity.Spec.Resources?.Limits?.Cpu == null)
        {
            return Fail("Kata runtime without CPU limits: SQL Server will see all VM vCPUs, which affects licensing. Set resources.limits.cpu.", 422);
        }

        // Topology spread & pool placement. A SingleNode instance is a single pod.
        if (TopologySpreadValidator.Validate(
                entity.Spec.TopologySpread, entity.Spec.AntiAffinity, entity.Spec.Pool,
                isSinglePod: entity.Spec.Mode == "SingleNode", warnings) is { } topoErr)
            return Fail(topoErr, 422);

        // A grace period of 0 (or a few seconds) means SIGKILL while sqlservr is
        // checkpointing. Every restart then replays a crash recovery, and the data volume is left in
        // a state that takes longer to open — the opposite of what a short grace period is meant to
        // achieve.
        if (ShutdownValidator.Validate(entity.Spec.Shutdown) is { } shutdownErr)
            return Fail(shutdownErr, 422);

        // preUpdateBackup is accepted by the schema but implemented nowhere. Saying so is
        // the honest fix — a user who believes a backup is taken before each update is worse off than
        // one who knows it is not.
        if (entity.Spec.UpdateStrategy?.RollingUpdate is { PreUpdateBackup: true })
            warnings.Add("updateStrategy.rollingUpdate.preUpdateBackup has no effect: the operator does not take a backup " +
                         "before restarting the instance. Schedule your own backup (SqlAgentJob) before an update.");

        return Success(warnings.ToArray());
    }

    internal ValidationResult ValidateCpuPinning(SqlInstanceSpec spec)
    {
        if (spec.Resources?.CpuPinning != true)
            return Success();

        var limCpu = spec.Resources.Limits?.Cpu;

        if (limCpu == null)
            return Fail("cpuPinning requires resources.limits.cpu to be set", 422);

        if (limCpu.Contains('m'))
            return Fail("cpuPinning requires integer CPU values, not millicores (e.g. \"4\" not \"4000m\")", 422);

        if (!int.TryParse(limCpu, out _))
            return Fail($"cpuPinning requires an integer CPU value. Got: '{limCpu}'", 422);

        var reqCpu = spec.Resources.Requests?.Cpu;
        if (reqCpu != null && reqCpu != limCpu)
            return Fail("cpuPinning requires resources.requests.cpu == resources.limits.cpu", 422);

        return Success();
    }
}
