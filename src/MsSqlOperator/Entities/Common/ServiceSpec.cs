namespace MsSqlOperator.Entities.Common;

public class ServiceSpec
{
    // Anchored (the API server does not anchor patterns: "XNodePortY" matched), and no
    // ExternalName — such a Service has no selector, so it can never reach the instance.
    [System.ComponentModel.DataAnnotations.RegularExpression("^(ClusterIP|NodePort|LoadBalancer)$")]
    public string Type { get; set; } = "ClusterIP";
    [System.ComponentModel.DataAnnotations.Range(1024, 65535)]
    public int Port { get; set; } = 1433;
    public Dictionary<string, string>? Annotations { get; set; }
}
