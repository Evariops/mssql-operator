using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlInstanceSpec
{
    public string Mode { get; set; } = "SingleNode";
    public string Version { get; set; } = "2022";
    public string Edition { get; set; } = "Developer";
    public string? Image { get; set; }
    public string? ImageTag { get; set; }
    public ResourceRequirements? Resources { get; set; }
    public StorageSpec? Storage { get; set; }
    public ServiceSpec? Service { get; set; }
    public SecretReference? SaPasswordSecretRef { get; set; }
    public AntiAffinitySpec? AntiAffinity { get; set; }
    public UpdateStrategySpec? UpdateStrategy { get; set; }
    public ResourceReference? ConfigurationRef { get; set; }
    public bool EnableNetworkPolicy { get; set; }
}
