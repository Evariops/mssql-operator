using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlInstanceSpec
{
    public string Mode { get; set; } = "SingleNode";
    public string Version { get; set; } = "2022";
    public string Edition { get; set; } = "Developer";
    public string? Image { get; set; }
    public string? ImageTag { get; set; }
    public MemorySpec? Memory { get; set; }
    public ResourceRequirements? Resources { get; set; }
    public StorageSpec? Storage { get; set; }
    public ServiceSpec? Service { get; set; }
    public SecretReference? SaPasswordSecretRef { get; set; }
    public AntiAffinitySpec? AntiAffinity { get; set; }
    public UpdateStrategySpec? UpdateStrategy { get; set; }
    public ResourceReference? ConfigurationRef { get; set; }
    public NetworkPolicySpec? NetworkPolicy { get; set; }

    // Scheduling
    public Dictionary<string, string>? NodeSelector { get; set; }
    public List<V1Toleration>? Tolerations { get; set; }
    public string? PriorityClassName { get; set; }
    public string? RuntimeClassName { get; set; }

    /// <summary>Pod shutdown behaviour: grace period for clean checkpointing.</summary>
    public ShutdownSpec? Shutdown { get; set; }

    /// <summary>Optional PDB. maxUnavailable=0 makes node drains wait for an
    /// explicit, controlled eviction of the SQL pod — that is the point, and it does block
    /// drains until the pod is moved; enable knowingly.</summary>
    public PodDisruptionBudgetSpec? PodDisruptionBudget { get; set; }

    // Security
    public V1PodSecurityContext? PodSecurityContext { get; set; }
    public V1SecurityContext? SecurityContext { get; set; }

    // Crypto
    public SecretReference? MachineKeySecretRef { get; set; }
    public SecretReference? SmkBackupPasswordSecretRef { get; set; }
}
