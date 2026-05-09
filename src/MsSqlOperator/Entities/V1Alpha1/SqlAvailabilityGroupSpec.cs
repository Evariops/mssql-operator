using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupSpec
{
    public int Replicas { get; set; } = 3;
    public int SynchronousReplicas { get; set; } = 2;
    public bool ReadableSecondaries { get; set; } = true;
    public AgInstanceTemplateSpec? InstanceTemplate { get; set; }
    public AgListenerSpec? Listener { get; set; }
    public AgListenerSpec? ReadOnlyListener { get; set; }
    public SecretReference? SaPasswordSecretRef { get; set; }
    public SecretReference? MirroringCertificateSecretRef { get; set; }
    public AntiAffinitySpec? AntiAffinity { get; set; }
    public FailoverSpec? Failover { get; set; }
    public UpdateStrategySpec? UpdateStrategy { get; set; }
    public List<AgDatabaseSpec>? Databases { get; set; }
    public bool EnableNetworkPolicy { get; set; }
}

public class AgInstanceTemplateSpec
{
    public string Version { get; set; } = "2022";
    public string Edition { get; set; } = "Enterprise";
    public ResourceRequirements? Resources { get; set; }
    public StorageSpec? Storage { get; set; }
    public ResourceReference? ConfigurationRef { get; set; }
}

public class AgListenerSpec
{
    public int Port { get; set; } = 1433;
    public ServiceSpec? Service { get; set; }
}

public class FailoverSpec
{
    public bool Automatic { get; set; } = true;
    public int HealthCheckTimeoutSeconds { get; set; } = 30;
    public int FailoverConditionLevel { get; set; } = 3;
}

public class AgDatabaseSpec
{
    public string Name { get; set; } = string.Empty;
    public string SeedingMode { get; set; } = "Automatic";
}
