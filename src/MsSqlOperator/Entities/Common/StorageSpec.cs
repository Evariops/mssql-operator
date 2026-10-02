namespace MsSqlOperator.Entities.Common;

public class StorageSpec
{
    // An optional volume is a volumeClaimTemplate the StatefulSet cannot gain or lose, and
    // mssql.conf moves master/errorlog onto it — adding or removing one needs a migration.
    private const string OptionalVolume = "an optional volume cannot be added to or removed from an existing resource";

    public StorageVolumeSpec? Data { get; set; }
    [MsSqlOperator.Operator.Abstractions.ImmutablePresence(OptionalVolume)]
    public StorageVolumeSpec? SysDb { get; set; }
    public StorageVolumeSpec? Log { get; set; }
    [MsSqlOperator.Operator.Abstractions.ImmutablePresence(OptionalVolume)]
    public StorageVolumeSpec? TempDb { get; set; }
    public StorageVolumeSpec? Backup { get; set; }
    [MsSqlOperator.Operator.Abstractions.ImmutablePresence(OptionalVolume)]
    public StorageVolumeSpec? ErrorLogs { get; set; }
    [MsSqlOperator.Operator.Abstractions.ImmutablePresence(OptionalVolume)]
    public StorageVolumeSpec? CertBackups { get; set; }
}

public class StorageVolumeSpec
{
    public string Size { get; set; } = "10Gi";
    [MsSqlOperator.Operator.Abstractions.Immutable("storageClassName is immutable: changing the storage class requires a data migration")]
    public string? StorageClassName { get; set; }
    public List<string>? AccessModes { get; set; }
}
