using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlInstance))]
public class SqlInstanceValidator : ValidationWebhook<SqlInstance>
{
    public override ValidationResult Create(SqlInstance entity, bool dryRun)
    {
        return Validate(entity);
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

        // Reject version downgrades
        if (oldEntity.Spec.Version != entity.Spec.Version &&
            !SqlServerImages.IsValidUpgrade(oldEntity.Spec.Version, entity.Spec.Version))
        {
            return Fail($"Version downgrade from {oldEntity.Spec.Version} to {entity.Spec.Version} is not supported.", 422);
        }

        // Strict immutability for fields without a safe
        // orchestration; storage GROWTH is orchestrated (PVC patch + STS recreate), shrink is not.
        if (!string.Equals(oldEntity.Spec.Mode, entity.Spec.Mode, StringComparison.Ordinal))
            return Fail("spec.mode is immutable: converting between SingleNode and AvailabilityGroupMember is not an in-place operation.", 422);

        if (ValidateStorageMutation(oldEntity.Spec.Storage, entity.Spec.Storage) is { } storageErr)
            return storageErr;

        return Success();
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

        if (!SqlServerImages.IsValidCombination(entity.Spec.Version, entity.Spec.Edition))
        {
            return Fail($"Invalid version/edition combination: {entity.Spec.Version}/{entity.Spec.Edition}", 422);
        }

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

        return Success();
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
