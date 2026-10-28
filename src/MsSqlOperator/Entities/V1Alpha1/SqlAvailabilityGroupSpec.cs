using System.ComponentModel.DataAnnotations;
using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public MsSqlOperator.Entities.Common.AutomationSpec? Automation { get; set; }

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
    public AgReadOnlyListenerSpec? ReadOnlyListener { get; set; }

    /// <summary>Read-Only routing configuration (Enterprise only).</summary>
    public AgReadOnlyRoutingSpec? ReadOnlyRouting { get; set; }

    /// <summary>Failover behavior and split-brain protection.</summary>
    public AgFailoverSpec? Failover { get; set; }

    /// <summary>Rolling update strategy (AG-aware).</summary>
    public UpdateStrategySpec? UpdateStrategy { get; set; }

    /// <summary>Pod anti-affinity rules.</summary>
    public AntiAffinitySpec? AntiAffinity { get; set; }

    /// <summary>Topology spread &amp; cross-instance placement. When neither this nor
    /// <see cref="AntiAffinity"/> is set, AG replicas default to a preferred node+zone self-spread.</summary>
    public TopologySpreadSpec? TopologySpread { get; set; }

    /// <summary>Cross-resource grouping label for pool-scoped spread / anti-affinity.</summary>
    public string? Pool { get; set; }

    /// <summary>CiliumNetworkPolicy configuration for SQL + mirroring ports.</summary>
    public NetworkPolicySpec? NetworkPolicy { get; set; }

    /// <summary>Enterprise only: AUTOMATED_BACKUP_PREFERENCE. Primary | Secondary | Prefer_Primary | None.</summary>
    public string? BackupPreference { get; set; }

    /// <summary>Online growth of the replicas' data and log volumes when they fill up.</summary>
    public AgVolumeAutoGrowSpec? VolumeAutoGrow { get; set; }
}

// ── Replica topology ──────────────────────────────────────

public class AgReplicaTopologySpec
{
    /// <summary>Total number of replicas. Standard: must be 2. Enterprise: 2-9.</summary>
    // The immutability of replicas.count was enforced by the webhook alone. A CEL rule
    // on the CRD holds even when the webhook is disabled or set to failurePolicy=Ignore, and the
    // controller re-checks it against the live StatefulSet (defence in depth): raising the count
    // spawns a pod on a blank PV that never joins the AG, lowering it silently drops a replica —
    // possibly the current primary.
    [MsSqlOperator.Operator.Abstractions.Immutable("spec.replicas.count is immutable: AG scale-out/in is not orchestrated. Recreate the AG to change the replica count.")]
    public int Count { get; set; } = 3;

    /// <summary>Number of replicas in SYNCHRONOUS_COMMIT mode (including primary).</summary>
    public int SynchronousCount { get; set; } = 2;

    /// <summary>Number of synchronized secondaries required before commit returns.
    /// 0 = read-scale, 1+ = data protection.</summary>
    public int RequiredSynchronizedSecondariesToCommit { get; set; } = 1;

    /// <summary>
    /// Durability floor: the operator will never lower
    /// <c>REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT</c> below this value to keep the group
    /// write-available.
    ///
    /// The operator lowers that setting when a synchronous secondary drops, so commits do not freeze — a
    /// trade of durability for availability. The README documented a "floor" to opt out of that trade;
    /// no such setting existed, and the lowering was unconditional, down to 0 (commits acknowledged by
    /// the primary alone). Deployments that must never weaken durability set this to 1: writes then
    /// block, loudly, instead of silently becoming non-durable.
    /// </summary>
    public int RequiredSyncFloor { get; set; }

    /// <summary>
    /// The durability contract, fixed in the control record when the AG is created.
    /// <c>Quorum</c>: REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT never goes below 1 and every commit
    /// is hardened on a secondary before it is acknowledged — writes BLOCK when no secondary is
    /// synchronized, and no acknowledged commit is ever lost by an automatic failover. <c>Legacy</c>:
    /// the setting may go down to 0 to keep writes flowing; an unplanned failover can then lose up to
    /// SESSION_TIMEOUT + T_step + J of writes, and is automatic only with
    /// allowAutomaticDataLossFailover. Absent: Quorum for a new Enterprise AG of at least 3 replicas,
    /// Legacy otherwise (condition DurabilityAtRisk).
    /// </summary>
    [System.ComponentModel.DataAnnotations.RegularExpression("^(Quorum|Legacy)$")]
    public string? Durability { get; set; }

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

public class AgVolumeAutoGrowSpec
{
    /// <summary>Auto (default): a replica's data or log volume that is at least
    /// <see cref="ThresholdPercent"/> full is grown by <see cref="GrowPercent"/>, up to its ceiling.
    /// Disabled: never.</summary>
    public string Mode { get; set; } = "Auto";

    /// <summary>Volume usage (percent) from which it is grown. Defaults to 80.</summary>
    public int ThresholdPercent { get; set; } = 80;

    /// <summary>How much a volume grows each time, in percent of its size (at least 1Gi). Defaults to 50.</summary>
    public int GrowPercent { get; set; } = 50;

    /// <summary>Ceiling of each replica's data volume. Defaults to four times its declared size.</summary>
    public string? DataMaxSize { get; set; }

    /// <summary>Ceiling of each replica's log volume. Defaults to four times its declared size.</summary>
    public string? LogMaxSize { get; set; }

    /// <summary>Ceiling of each replica's backup volume, grown when a backup cannot be written to it.
    /// Defaults to four times its declared size.</summary>
    public string? BackupMaxSize { get; set; }
}

// ── Instance template ─────────────────────────────────────

public class AgInstanceTemplateSpec
{
    public string Version { get; set; } = "2022";

    /// <summary>The exact cumulative update every replica runs — <c>RTM</c>, <c>CU25</c>, or
    /// <c>CU32-GDR1</c>. One build for the whole AG is the point: replicas on different builds is
    /// precisely the state an AG cannot sustain, so this is what a rolling CU upgrade moves.
    /// Mutually exclusive with <c>imageTag</c>; a CU downgrade is rejected at admission.</summary>
    [RegularExpression("^(RTM|CU[0-9]+)(-GDR[0-9]*)?$")]
    public string? CumulativeUpdate { get; set; }

    /// <summary>The OS base every replica's image is built on — <c>ubuntu-22.04</c>,
    /// <c>ubuntu-24.04</c>. Restricted to the bases the declared <c>version</c> actually ships under.
    /// Omit it to follow the operator's default. Mutually exclusive with <c>imageTag</c>.</summary>
    [RegularExpression("^ubuntu-[0-9]{2}\\.[0-9]{2}$")]
    public string? BaseOs { get; set; }

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

    /// <summary>Annotations added to every replica's pod — for what a node agent reads from the pod, such as
    /// <c>cpu.preserve.resource-policy.nri.io: "true"</c>, which keeps an NRI resource policy from
    /// resizing SQL Server's cpuset under it. Keys of the operator's domain are refused.</summary>
    public Dictionary<string, string>? PodAnnotations { get; set; }

    /// <summary>Pod shutdown behaviour: grace period for clean checkpointing.</summary>
    public ShutdownSpec? Shutdown { get; set; }

    /// <summary>Probe budgets.</summary>
    public ProbesSpec? Probes { get; set; }

    // Security
    public V1PodSecurityContext? PodSecurityContext { get; set; }
    public V1SecurityContext? SecurityContext { get; set; }

    /// <summary>The certificate every replica presents; one Secret for all replicas, its SANs
    /// covering <c>*.&lt;ag&gt;-headless.&lt;namespace&gt;.svc.cluster.local</c>.</summary>
    public TlsSpec? Tls { get; set; }
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
    [MsSqlOperator.Operator.Abstractions.Immutable("endpoint.name is immutable in v1alpha1: recreating the mirroring endpoint interrupts replication")]
    public string Name { get; set; } = "Hadr_endpoint";

    [MsSqlOperator.Operator.Abstractions.Immutable("endpoint.port is immutable in v1alpha1: recreating the mirroring endpoint interrupts replication")]
    [System.ComponentModel.DataAnnotations.Range(1024, 65535)]
    public int Port { get; set; } = 5022;
    public string EncryptionAlgorithm { get; set; } = "AES";
}

// ── Listeners ─────────────────────────────────────────────

public class AgListenerSpec
{
    public int Port { get; set; } = 1433;
    public ServiceSpec? Service { get; set; }
}

public class AgReadOnlyListenerSpec : AgListenerSpec
{
    /// <summary>When no secondary can serve reads (all disconnected, not synchronized or not
    /// readable), the primary serves them through this listener, and ends the read-only routing
    /// list. True by default.</summary>
    public bool FallbackToPrimary { get; set; } = true;
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

    /// <summary>Deadline of one health pass over every replica, in seconds (at least 5). A replica
    /// that has not answered by then counts as unreachable for that pass.</summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 15;

    /// <summary>The failover condition level the operator implements: it fails over when the
    /// primary is down or does not answer; it does not act on SQL Server errors.</summary>
    public const int ImplementedFailoverConditionLevel = 2;

    /// <summary>Severity level triggering operator failover, on the SQL Server scale
    /// (1=ServerDown, 2=+Unresponsive, 3=+CriticalErrors, 4=+ModerateErrors, 5=+AnyFailure). Only
    /// level 2 is implemented; admission refuses the others.</summary>
    public int FailoverConditionLevel { get; set; } = ImplementedFailoverConditionLevel;

    /// <summary>Consecutive health passes without a reachable primary before the operator acts
    /// (drains the writer label, promotes a secondary). One missed pass is a
    /// monitoring blip, not an outage.</summary>
    public int PrimaryUnreachableThreshold { get; set; } = 3;

    public AgSplitBrainProtectionSpec? SplitBrainProtection { get; set; }

    /// <summary>Preferred failover targets by ordinal.</summary>
    public List<AgFailoverTargetSpec>? PreferredTargets { get; set; }

    /// <summary>Allow operator to automatically perform force-failover-allow-data-loss.</summary>
    public bool AllowAutomaticDataLossFailover { get; set; } = false;

    /// <summary>
    /// Upper bound (KB of unsent/un-redone log) on the data loss an AUTOMATIC forced failover may
    /// accept. When the freshest eligible replica is further behind than this — or
    /// when the lag cannot be measured — the operator stops and asks for a human decision instead
    /// of silently discarding an unbounded amount of committed transactions. Unset = no bound
    /// (previous behaviour). Ignored on manual force-failover-to, which is an explicit decision.
    /// </summary>
    public long? MaxDataLossKb { get; set; }

    /// <summary>
    /// A replica the primary has seen DISCONNECTED for this long, while it holds back the log of an
    /// AG database that is at least 85% full, is taken out of the AG so that the log can be truncated
    /// it is added back and re-seeded when it answers again. Requires automatic seeding.
    /// 0 never removes a replica. Defaults to 30.
    /// </summary>
    public int RemoveDisconnectedReplicaAfterMinutes { get; set; } = 30;

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
