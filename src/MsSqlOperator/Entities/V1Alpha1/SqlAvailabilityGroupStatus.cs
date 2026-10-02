using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAvailabilityGroupStatus
{
    public string Phase { get; set; } = Phases.Pending;
    public long ObservedGeneration { get; set; }
    public string? Edition { get; set; }

    /// <summary>The image the operator resolved for every replica of this AG.</summary>
    public string? Image { get; set; }

    /// <summary>The patch level that image deploys, e.g. <c>CU25</c>. Compare it with
    /// <c>primaryProductVersion</c> to see whether a rolling CU upgrade has actually landed.</summary>
    public string? CumulativeUpdate { get; set; }

    /// <summary>The OS base that image is built on, e.g. <c>ubuntu-22.04</c>.</summary>
    public string? BaseOs { get; set; }

    public string ClusterType { get; set; } = "None";
    public string? PrimaryReplica { get; set; }

    /// <summary>Consecutive health passes where no primary answered. Reset on the
    /// first healthy pass; failover actions trigger only at the spec threshold.</summary>
    public int PrimaryUnreachableCount { get; set; }

    /// <summary>When the primary was first observed unreachable. A pass counter is
    /// not a delay — every increment writes the status, which re-enqueues the AG immediately, so
    /// three "passes" could elapse in seconds. Failover requires BOTH the pass count and this
    /// wall-clock window. Reset on the first pass that finds a primary.</summary>
    public DateTime? PrimaryUnreachableSince { get; set; }

    /// <summary>Engine build of the last confirmed primary, e.g. "16.0.4125.3".
    /// A replica running an older major version is never promoted: it cannot redo the newer log.</summary>
    public string? PrimaryProductVersion { get; set; }

    /// <summary>Replicas that were SYNCHRONOUS_COMMIT and SYNCHRONIZED in the last report sourced
    /// from a primary. Once the primary is gone, the secondaries only see themselves as NOT
    /// SYNCHRONIZING; this record, with <see cref="LastRequiredSynchronizedSecondaries"/>, is what
    /// tells whether the most advanced survivor holds every committed transaction. Updated only when
    /// it changes.</summary>
    public List<string>? LastSynchronizedReplicas { get; set; }

    /// <summary>REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT last read on the primary: below
    /// 1, the primary could commit alone and no failover without it can be proven lossless.</summary>
    public int? LastRequiredSynchronizedSecondaries { get; set; }

    /// <summary>Fencing of the previous primary in progress: advanced by one bounded step
    /// per pass instead of being awaited inside a pass whose budget it exceeded.</summary>
    public AgFencingStatus? Fencing { get; set; }

    /// <summary>An annotation-requested failover that has not landed yet. The planned
    /// path keeps its annotation until the failover completes, so a transient error retries instead
    /// of silently discarding the request.</summary>
    public AgPendingFailoverStatus? PendingFailover { get; set; }

    /// <summary>True while reconciliation is paused by the operator annotation:
    /// health checks, failover and rolling updates are NOT running.</summary>
    public bool ReconcilePaused { get; set; }

    public string? PrimaryEndpoint { get; set; }
    public string? ReadOnlyEndpoint { get; set; }

    public MemoryStatus? Memory { get; set; }

    public List<AgReplicaStatus>? Replicas { get; set; }

    /// <summary>Aggregated summary of SqlAvailabilityGroupDatabase CRDs referencing this AG.</summary>
    public AgDatabaseSummaryStatus? DatabaseSummary { get; set; }

    public List<AgCertificateStatus>? Certificates { get; set; }
    public AgLastFailoverStatus? LastFailover { get; set; }

    /// <summary>Progress of an in-flight AG-aware rolling update.</summary>
    public AgRollingUpdateStatus? RollingUpdate { get; set; }

    /// <summary>
    /// Signature of the read-only routing topology last applied to SQL Server (primary + readable
    /// secondaries + port). Lets the controller re-apply routing only when the topology actually
    /// changes (failover, replica add/remove, readability flip) instead of every cycle.
    /// </summary>
    /// <summary>Internal idempotency marker (undocumented format) — lets the
    /// controller skip re-applying read-only routing when the topology is unchanged. Not a
    /// user-facing field; treat as opaque.</summary>
    public string? ReadOnlyRoutingSignature { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}

public class AgRollingUpdateStatus
{
    /// <summary>Idle|Planning|UpdatingSecondary|FailingOverForUpdate|UpdatingPrimary|Completing|Failed.</summary>
    public string Phase { get; set; } = RollingUpdatePhases.Idle;

    /// <summary>The pod currently being updated (deleted and awaited), if any.</summary>
    public string? CurrentPod { get; set; }

    /// <summary>Fine-grained step for the current pod: DeletingPod|WaitingHealthy|Pausing.</summary>
    public string? Step { get; set; }

    public int TotalPods { get; set; }
    public int UpdatedPods { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CurrentStepStartedAt { get; set; }

    /// <summary>Earliest time the next pod may be started (pauseBetweenPodsSeconds gate).</summary>
    public DateTime? NextActionAfter { get; set; }

    /// <summary>Since when the rollout has been unable to start the next pod because the AG is not
    /// healthy enough (or a full-text population is running). Bounds a state that
    /// previously had no timeout at all and could hang InProgress forever.</summary>
    public DateTime? BlockedSince { get; set; }

    /// <summary>The StatefulSet updateRevision this rollout converges toward.</summary>
    public string? TargetRevision { get; set; }

    /// <summary>The updated secondary the rollout asked the failover orchestrator to promote before
    /// updating the primary (requested here, no longer by writing the user's failover-to
    /// annotation over the manifest). Cleared once the request is consumed.</summary>
    public string? FailoverTarget { get; set; }
}

public class AgReplicaStatus
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Unknown";
    public string AvailabilityMode { get; set; } = "Unknown";
    public string SynchronizationState { get; set; } = "Unknown";
    public string ConnectionState { get; set; } = "Unknown";
    public bool IsFailoverReady { get; set; }
    public bool Readable { get; set; }
    public string ListenerReady { get; set; } = "none";

    // No live telemetry in the status. LastHeartbeat /
    // LogSendQueueSize / RedoQueueSize changed on every reconcile, so every reconcile wrote
    // the status, so every write triggered a watch event, so every event triggered a
    // reconcile — a permanent etcd hot-loop. Freshness and lag live in the agreplicastates
    // view API and the Prometheus gauges; the status only keeps convergence state.
}

public class AgCertificateStatus
{
    public string? ReplicaName { get; set; }
    public string? Name { get; set; }
    public string? Thumbprint { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public List<string>? TrustedByReplicas { get; set; }
}

public class AgLastFailoverStatus
{
    public DateTime? Timestamp { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Reason { get; set; }
    public bool DataLoss { get; set; }

    /// <summary>Unsent + un-redone log (KB) the promoted replica was missing at promotion time
    /// 0 on a lossless failover; null when it could not be measured. This is the
    /// blast radius of a forced failover — previously never recorded anywhere.</summary>
    public long? UnsentLogKb { get; set; }
}

public class AgFencingStatus
{
    /// <summary>The pod being fenced (the previous primary).</summary>
    public string Pod { get; set; } = string.Empty;

    /// <summary>UID of the pod instance the fencing targets: a pod recreated under the same name
    /// (new UID) is a different instance and is never fenced on this record.</summary>
    public string? PodUid { get; set; }

    /// <summary>GracefulDeletion | StorageDetach.</summary>
    public string Stage { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    /// <summary>When the current stage gives up waiting.</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>PersistentVolumes whose VolumeAttachments are being removed (StorageDetach).</summary>
    public List<string>? Volumes { get; set; }

    /// <summary>The replica to promote once the fencing is confirmed.</summary>
    public string? Target { get; set; }

    /// <summary>Whether the promotion accepts data loss (decided before fencing started).</summary>
    public bool AllowDataLoss { get; set; }

    public string? Reason { get; set; }
}

public class AgPendingFailoverStatus
{
    public string? Target { get; set; }
    public DateTime? RequestedAt { get; set; }
    public int Attempts { get; set; }
}

public class AgDatabaseSummaryStatus
{
    public int Total { get; set; }
    public int Synchronized { get; set; }
    public int Synchronizing { get; set; }
    public int Suspended { get; set; }
    public int Failed { get; set; }
    // Per-database entries removed: unbounded by database count. The counts above are the
    // O(1) roll-up; per-database phase is served live by the aggregated APIService. Per-replica lists
    // (replicas[], certificates[]) are kept: they are bounded by replica count (<= 9).
}
