using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// Slim, etcd-light status: convergence signal + O(1) roll-up scalars only. The previous
/// per-target tree (rotation history, evidences, per-database rotation state, per-replica state) is
/// gone — it grew past etcd's per-object limit. Per-database encryption state and per-replica state are
/// served live by the aggregated APIService (<c>tdedatabasestates</c>); rotation/backup occurrences are
/// Events; counts are metrics; backup evidence is the immutable Secret/S3 objects themselves.
/// </summary>
public class SqlTdeCertificateStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }

    /// <summary>Targets (instances/AGs) matched by the selector.</summary>
    public int Targets { get; set; }

    /// <summary>Active certificate version + thumbprint currently protecting databases.</summary>
    public string? ActiveVersion { get; set; }
    public string? ActiveThumbprint { get; set; }

    /// <summary>Candidate (in-rotation) certificate thumbprint, when a rotation is in flight.</summary>
    public string? CandidateThumbprint { get; set; }

    /// <summary>Coarse rotation phase: Idle | Preparing | Rotating | RetentionWaiting | Failed.</summary>
    public string RotationPhase { get; set; } = "Idle";
    public DateTime? LastRotationTime { get; set; }
    public DateTime? RetireNotBefore { get; set; }
    public bool RetirementPendingApproval { get; set; }

    /// <summary>Roll-up of database encryption across all targets.</summary>
    public int EncryptedDatabases { get; set; }
    public int PendingDatabases { get; set; }
    public int FailedDatabases { get; set; }

    /// <summary>DEK regenerations still owed after a protector move (<c>{kind}/{target}/{database}</c>):
    /// deferred by <c>maxParallelDatabasesPerTarget</c> or waiting for a log backup. Empty in the
    /// steady state.</summary>
    public List<string>? PendingDekRegenerations { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
