using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlTdeCertificateStatus
{
    public bool CertificateCreated { get; set; }
    public List<TdeCertificateTargetStatus>? TargetStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class TdeCertificateTargetStatus
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool CertificateCreated { get; set; }
    public string? CertificateExpiry { get; set; }
    public string? Thumbprint { get; set; }
    public List<EncryptedDatabaseStatus>? EncryptedDatabases { get; set; }
    public string? LastBackup { get; set; }
    public string? LastError { get; set; }
    public List<TdeReplicaStatus>? ReplicaStatuses { get; set; }
}

public class TdeReplicaStatus
{
    public string Replica { get; set; } = string.Empty;
    public bool CertificateCreated { get; set; }
    public bool MasterKeyCreated { get; set; }
    public string? Thumbprint { get; set; }
    public string? LastError { get; set; }
}

public class EncryptedDatabaseStatus
{
    public string Name { get; set; } = string.Empty;
    public string? EncryptionState { get; set; }
    public string? EncryptionAlgorithm { get; set; }
}
