namespace MsSqlOperator.Entities.Common;

public class StorageSpec
{
    public StorageVolumeSpec? Data { get; set; }
    public StorageVolumeSpec? Log { get; set; }
    public StorageVolumeSpec? TempDb { get; set; }
    public StorageVolumeSpec? Backup { get; set; }
}

public class StorageVolumeSpec
{
    public string Size { get; set; } = "10Gi";
    public string? StorageClassName { get; set; }
}
