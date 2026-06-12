using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupSpec
{
    /// <summary>Standard | Enterprise — determines constraints and available features.</summary>
    public string Edition { get; set; } = "Enterprise";

    /// <summary>Replica topology (count, sync mode, overrides).</summary>
    public AgReplicaTopologySpec Replicas { get; set; } = new();

    /// <summary>Default settings applied to all replicas unless overridden.</summary>
    public AgReplicaDefaultsSpec? ReplicaDefaults { get; set; }

    /// <summary>SQL Server instance template (image, resources, storage).</summary>
    public AgInstanceTemplateSpec? InstanceTemplate { get; set; }

    // Databases are decoupled into SqlAvailabilityGroupDatabase CRDs

    /// <summary>HADR certificate for endpoint authentication (X.509).</summary>
    public AgHadrCertificateSpec? HadrCertificate { get; set; }

    /// <summary>Database mirroring endpoint configuration.</summary>
    public AgEndpointSpec? Endpoint { get; set; }

    /// <summary>Secret containing the Database Master Key password.</summary>
    public SecretReference? MasterKeyPasswordSecretRef { get; set; }

    /// <summary>Secret containing the SA password for all replicas.</summary>
    public SecretReference? SaPasswordSecretRef { get; set; }

    /// <summary>Secret containing the machine key for all replicas (migration scenario).</summary>
    public SecretReference? MachineKeySecretRef { get; set; }

    /// <summary>Secret containing the SMK backup encryption password.</summary>
    public SecretReference? SmkBackupPasswordSecretRef { get; set; }

    /// <summary>Optional dedicated SQL login for operator (instead of SA).</summary>
    public SqlLoginSecretReference? OperatorLoginSecretRef { get; set; }

    /// <summary>Read/Write listener service.</summary>
    public AgListenerSpec? Listener { get; set; }

    /// <summary>Read-Only listener service (Enterprise only, ignored for Standard).</summary>
    public AgListenerSpec? ReadOnlyListener { get; set; }

    /// <summary>Read-Only routing configuration (Enterprise only).</summary>
    public AgReadOnlyRoutingSpec? ReadOnlyRouting { get; set; }

    /// <summary>Failover behavior and split-brain protection.</summary>
    public AgFailoverSpec? Failover { get; set; }

    /// <summary>Rolling update strategy (AG-aware).</summary>
    public UpdateStrategySpec? UpdateStrategy { get; set; }

    /// <summary>Pod anti-affinity rules.</summary>
    public AntiAffinitySpec? AntiAffinity { get; set; }

    /// <summary>CiliumNetworkPolicy configuration for SQL + mirroring ports.</summary>
    public NetworkPolicySpec? NetworkPolicy { get; set; }

    /// <summary>Enterprise only: AUTOMATED_BACKUP_PREFERENCE. Primary | Secondary | Prefer_Primary | None.</summary>
    public string? BackupPreference { get; set; }
}

// ── Replica topology ──────────────────────────────────────

public class AgReplicaTopologySpec
{
    /// <summary>Total number of replicas. Standard: must be 2. Enterprise: 2-9.</summary>
    public int Count { get; set; } = 3;

    /// <summary>Number of replicas in SYNCHRONOUS_COMMIT mode (including primary).</summary>
    public int SynchronousCount { get; set; } = 2;

    /// <summary>Number of synchronized secondaries required before commit returns.
    /// 0 = read-scale, 1+ = data protection.</summary>
    public int RequiredSynchronizedSecondariesToCommit { get; set; } = 1;

    /// <summary>Per-replica overrides (by ordinal index).</summary>
    public List<AgReplicaOverrideSpec>? Overrides { get; set; }
}

public class AgReplicaOverrideSpec
{
    /// <summary>Pod ordinal index (0-based).</summary>
    public int Ordinal { get; set; }

    /// <summary>SynchronousCommit | AsynchronousCommit.</summary>
    public string? AvailabilityMode { get; set; }

    /// <summary>Enterprise only: No | ReadOnly | All.</summary>
    public string? ReadableSecondary { get; set; }

    /// <summary>Enterprise only: backup priority 0-100.</summary>
    public int? BackupPriority { get; set; }

    /// <summary>Session timeout in seconds.</summary>
    public int? SessionTimeout { get; set; }
}

// ── Replica defaults ──────────────────────────────────────

public class AgReplicaDefaultsSpec
{
    public string AvailabilityMode { get; set; } = "SynchronousCommit";
    public string ReadableSecondary { get; set; } = "All";
    public string SeedingMode { get; set; } = "Automatic";
    public int BackupPriority { get; set; } = 50;
    public int SessionTimeout { get; set; } = 10;
}

// ── Instance template ─────────────────────────────────────

public class AgInstanceTemplateSpec
{
    public string Version { get; set; } = "2022";
    public string? Image { get; set; }
    public string? ImageTag { get; set; }
    public MemorySpec? Memory { get; set; }
    public ResourceRequirements? Resources { get; set; }
    public StorageSpec? Storage { get; set; }
    public ResourceReference? ConfigurationRef { get; set; }

    // Scheduling
    public Dictionary<string, string>? NodeSelector { get; set; }
    public List<V1Toleration>? Tolerations { get; set; }
    public string? PriorityClassName { get; set; }
    public string? RuntimeClassName { get; set; }

    // Security
    public V1PodSecurityContext? PodSecurityContext { get; set; }
    public V1SecurityContext? SecurityContext { get; set; }
}

// ── HADR Certificate ──────────────────────────────────────

public class AgHadrCertificateSpec
{
    /// <summary>Generate | Import.</summary>
    public string Source { get; set; } = "Generate";

    /// <summary>PerReplica (each replica gets its own cert, public keys cross-imported).</summary>
    public string Model { get; set; } = "PerReplica";

    public CertificateGenerateSpec? Generate { get; set; }
    public CertificateImportSpec? ImportFrom { get; set; }

    /// <summary>K8s Secret where the operator stores/reads the certificate.</summary>
    public string? SecretName { get; set; }

    /// <summary>Password used to encrypt the private key during export.</summary>
    public SecretReference? EncryptionPasswordSecretRef { get; set; }
}

public class CertificateGenerateSpec
{
    public string Subject { get; set; } = string.Empty;
    public string? ExpiryDate { get; set; }
    public int KeyLength { get; set; } = 2048;
}

public class CertificateImportSpec
{
    public string SecretName { get; set; } = string.Empty;
    public string CertificateKey { get; set; } = "certificate.cer";
    public string PrivateKeyKey { get; set; } = "private-key.pvk";
    public string? PasswordKey { get; set; }
}

// ── Endpoint ──────────────────────────────────────────────

public class AgEndpointSpec
{
    public string Name { get; set; } = "Hadr_endpoint";
    public int Port { get; set; } = 5022;
    public string EncryptionAlgorithm { get; set; } = "AES";
}

// ── Listeners ─────────────────────────────────────────────

public class AgListenerSpec
{
    public int Port { get; set; } = 1433;
    public ServiceSpec? Service { get; set; }
}

// ── Read-Only Routing (Enterprise) ────────────────────────

public class AgReadOnlyRoutingSpec
{
    public bool Enabled { get; set; }
}

// ── Failover ──────────────────────────────────────────────

public class AgFailoverSpec
{
    /// <summary>Whether the operator manages failovers (always MANUAL at SQL Server level).</summary>
    public bool OperatorManaged { get; set; } = true;

    public int HealthCheckIntervalSeconds { get; set; } = 10;
    public int HealthCheckTimeoutSeconds { get; set; } = 30;

    /// <summary>1-5: severity level triggering operator failover.
    /// 1=ServerDown, 2=+Unresponsive, 3=+CriticalErrors, 4=+ModerateErrors, 5=+AnyFailure.</summary>
    public int FailoverConditionLevel { get; set; } = 3;

    /// <summary>Consecutive health passes without a reachable primary before the operator acts
    /// (drains the writer label, promotes a secondary). One missed pass is a
    /// monitoring blip, not an outage.</summary>
    public int PrimaryUnreachableThreshold { get; set; } = 3;

    public AgSplitBrainProtectionSpec? SplitBrainProtection { get; set; }

    /// <summary>Preferred failover targets by ordinal.</summary>
    public List<AgFailoverTargetSpec>? PreferredTargets { get; set; }

    /// <summary>Allow operator to automatically perform force-failover-allow-data-loss.</summary>
    public bool AllowAutomaticDataLossFailover { get; set; } = false;

    /// <summary>After failover, automatically resume suspended databases.</summary>
    public bool AutoResumeDataMovement { get; set; } = true;

    /// <summary>After failover, automatically update pod labels.</summary>
    public bool AutoReconfigurePrimary { get; set; } = true;
}

public class AgSplitBrainProtectionSpec
{
    public bool Enabled { get; set; } = true;

    /// <summary>NetworkIsolation | ManualApproval | External.</summary>
    public string FencingStrategy { get; set; } = "NetworkIsolation";

    public int FencingTimeoutSeconds { get; set; } = 60;
}

public class AgFailoverTargetSpec
{
    public int Ordinal { get; set; }
}
