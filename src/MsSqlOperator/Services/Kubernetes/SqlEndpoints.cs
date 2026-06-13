namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Single source of truth for SQL endpoint construction. The invariant: connecting
/// to a POD (headless FQDN) always uses the container port 1433; only connections through the
/// SERVICE use <c>serviceSpec.Port</c>. Mixing them broke as soon as the Service port was
/// customized.
/// </summary>
public static class SqlEndpoints
{
    /// <summary>SQL Server's in-container port — what every pod FQDN connection must use.</summary>
    public const int ContainerPort = 1433;

    /// <summary>Pod FQDN of a StatefulSet ordinal through its governing (headless) service.</summary>
    public static string PodFqdn(string stsName, int ordinal, string serviceName, string ns)
        => $"{stsName}-{ordinal}.{serviceName}.{ns}.svc.cluster.local";

    /// <summary>Pod FQDN for a SqlInstance (service name == instance name).</summary>
    public static string InstancePodFqdn(string instanceName, string ns)
        => PodFqdn(instanceName, 0, instanceName, ns);

    /// <summary>Service FQDN of a SqlInstance — pair with the Service port, never 1433 blindly.</summary>
    public static string ServiceFqdn(string instanceName, string ns)
        => $"{instanceName}.{ns}.svc.cluster.local";
}
