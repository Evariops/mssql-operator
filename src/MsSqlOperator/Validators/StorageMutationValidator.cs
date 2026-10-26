using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Validators;

/// <summary>
/// What an update may change in the storage of an existing SqlInstance or availability group. Every
/// volume is a volumeClaimTemplate of the StatefulSet: growth is orchestrated (PVCs patched, the
/// StatefulSet recreated around its pods), anything else needs a data migration.
/// </summary>
internal static class StorageMutationValidator
{
    /// <summary>The rejection message, or null when the change is allowed.</summary>
    public static string? Validate(StorageSpec? oldStorage, StorageSpec? newStorage, string path)
    {
        foreach (var (name, oldVol, newVol) in Volumes(oldStorage, newStorage))
        {
            // An added sysDb or errorLogs volume moves masterdatafile/errorlogfile in mssql.conf,
            // while the StatefulSet cannot carry the new volumeClaimTemplate — the next restart read a
            // mssql.conf pointing at an unmounted path (a crash, or a blank master on a writable root).
            // Removing one is the mirror image.
            if (oldVol is null != newVol is null)
                return $"{path}.{name} cannot be {(newVol is null ? "removed from" : "added to")} an existing resource: " +
                       "the StatefulSet's volume claims are immutable, and SQL Server would look for its files on a volume " +
                       "that is not mounted. Recreate the resource (restoring its databases) to change the set of volumes.";
            if (oldVol is null || newVol is null)
                continue;

            if (!string.Equals(oldVol.StorageClassName, newVol.StorageClassName, StringComparison.Ordinal))
                return $"{path}.{name}.storageClassName is immutable: changing the storage class requires a data migration.";

            if (!AccessModes(oldVol).SequenceEqual(AccessModes(newVol), StringComparer.Ordinal))
                return $"{path}.{name}.accessModes is immutable: the StatefulSet's volume claims cannot change.";

            if (oldVol.Size != newVol.Size
                && MemoryCalculator.ParseToMb(newVol.Size) < MemoryCalculator.ParseToMb(oldVol.Size))
                return $"{path}.{name}.size cannot shrink (Kubernetes cannot reduce a PVC); growth is orchestrated automatically.";
        }

        return null;
    }

    /// <summary>Each volume before and after. data, log and backup are always provisioned — with the
    /// builder's default size when the spec leaves them out — so an omitted one compares as its default.</summary>
    private static IEnumerable<(string Name, StorageVolumeSpec? Old, StorageVolumeSpec? New)> Volumes(StorageSpec? o, StorageSpec? n)
    {
        yield return ("data", o?.Data ?? StatefulSetBuilder.DefaultDataVolume, n?.Data ?? StatefulSetBuilder.DefaultDataVolume);
        yield return ("log", o?.Log ?? StatefulSetBuilder.DefaultLogVolume, n?.Log ?? StatefulSetBuilder.DefaultLogVolume);
        yield return ("backup", o?.Backup ?? StatefulSetBuilder.DefaultBackupVolume, n?.Backup ?? StatefulSetBuilder.DefaultBackupVolume);
        yield return ("sysDb", o?.SysDb, n?.SysDb);
        yield return ("tempDb", o?.TempDb, n?.TempDb);
        yield return ("errorLogs", o?.ErrorLogs, n?.ErrorLogs);
        yield return ("certBackups", o?.CertBackups, n?.CertBackups);
    }

    private static IEnumerable<string> AccessModes(StorageVolumeSpec volume) => volume.AccessModes ?? ["ReadWriteOnce"];
}
