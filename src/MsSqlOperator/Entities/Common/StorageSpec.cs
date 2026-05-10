namespace MsSqlOperator.Entities.Common;

public class StorageSpec
{
    public StorageVolumeSpec? Data { get; set; }
    public StorageVolumeSpec? SysDb { get; set; }
    public StorageVolumeSpec? Log { get; set; }
    public StorageVolumeSpec? TempDb { get; set; }
    public StorageVolumeSpec? Backup { get; set; }
    public StorageVolumeSpec? ErrorLogs { get; set; }
    public StorageVolumeSpec? CertBackups { get; set; }
}

public class StorageVolumeSpec
{
    public string Size { get; set; } = "10Gi";
    public string? StorageClassName { get; set; }
    public List<string>? AccessModes { get; set; }
}
