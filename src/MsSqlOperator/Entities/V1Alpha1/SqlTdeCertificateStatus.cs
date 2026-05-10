using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlTdeCertificateStatus
{
    public List<TdeCertificateTargetStatus>? TargetStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class TdeCertificateTargetStatus
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;

    public CertificateVersionStatus? ActiveVersion { get; set; }
    public CertificateVersionStatus? CandidateVersion { get; set; }

    public DatabaseSelectionSnapshotStatus? DatabaseSelection { get; set; }
    public RotationStatus? Rotation { get; set; }
    public List<TdeReplicaStatus>? ReplicaStatuses { get; set; }
    public string? LastError { get; set; }
}

public class CertificateVersionStatus
{
    public string SourceType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SqlCertificateName { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
    public string? SourceSecretName { get; set; }
}

public class DatabaseSelectionSnapshotStatus
{
    public DateTime SelectedAt { get; set; }
    public long ObservedGeneration { get; set; }
    public string SelectionHash { get; set; } = string.Empty;
    public List<DatabaseRotationStatus> Databases { get; set; } = [];
}

public class DatabaseRotationStatus
{
    public string Name { get; set; } = string.Empty;
    public string Phase { get; set; } = "Pending";
    public string? EncryptionState { get; set; }
    public string? ProtectorThumbprint { get; set; }
    public string? LastError { get; set; }
}

public class RotationStatus
{
    public string Phase { get; set; } = "Idle";
    public string Mode { get; set; } = "ProtectorOnly";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? RetireNotBefore { get; set; }
    public List<CertificateEvidenceStatus>? Evidences { get; set; }
    public string? LastError { get; set; }
    public int Attempt { get; set; }
    public bool RetirementPendingApproval { get; set; }
    public bool RetirementBlockedByBackupEvidence { get; set; }
    public List<RotationHistoryEntry>? History { get; set; }
}

public class RotationHistoryEntry
{
    public string Version { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Result { get; set; } = string.Empty;
    public int DatabasesRotated { get; set; }
    public int DatabasesFailed { get; set; }
    public string? Error { get; set; }
}

public class CertificateEvidenceStatus
{
    public string Type { get; set; } = string.Empty;
    public string? Namespace { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string Thumbprint { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public string? S3ObjectKey { get; set; }
    public DateTime ObservedAt { get; set; }
    public bool Verified { get; set; }
    public bool Immutable { get; set; }
}

public class TdeReplicaStatus
{
    public string Replica { get; set; } = string.Empty;
    public bool CertificateCreated { get; set; }
    public bool MasterKeyCreated { get; set; }
    public string? Thumbprint { get; set; }
    public string? LastError { get; set; }
}
