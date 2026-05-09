namespace MsSqlOperator.Entities.Common;

public class ServiceSpec
{
    public string Type { get; set; } = "ClusterIP";
    public int Port { get; set; } = 1433;
    public Dictionary<string, string>? Annotations { get; set; }
}
