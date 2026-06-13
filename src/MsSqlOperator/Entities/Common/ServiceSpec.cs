namespace MsSqlOperator.Entities.Common;

public class ServiceSpec
{
    [System.ComponentModel.DataAnnotations.RegularExpression("ClusterIP|NodePort|LoadBalancer|ExternalName")]
    public string Type { get; set; } = "ClusterIP";
    [System.ComponentModel.DataAnnotations.Range(1024, 65535)]
    public int Port { get; set; } = 1433;
    public Dictionary<string, string>? Annotations { get; set; }
}
