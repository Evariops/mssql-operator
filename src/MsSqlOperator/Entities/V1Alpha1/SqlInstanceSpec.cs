using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlInstanceSpec
{
    [MsSqlOperator.Operator.Abstractions.Immutable("spec.mode is immutable: converting between SingleNode and AvailabilityGroupMember is not an in-place operation")]
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

    /// <summary>Topology spread &amp; cross-instance placement.</summary>
    public TopologySpreadSpec? TopologySpread { get; set; }

    /// <summary>Cross-resource grouping label for pool-scoped spread / anti-affinity.</summary>
    public string? Pool { get; set; }

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

    /// <summary>Probe budgets. Tolerant defaults; raise them for instances whose
    /// login latency spikes under load, so a slowdown is not amplified into an outage.</summary>
    public ProbesSpec? Probes { get; set; }

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
