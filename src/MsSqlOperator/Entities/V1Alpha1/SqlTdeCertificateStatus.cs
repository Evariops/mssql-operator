using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlTdeCertificateStatus
{
    public bool CertificateCreated { get; set; }
    public DateTime? CertificateExpiry { get; set; }
    public string? Thumbprint { get; set; }
    public List<EncryptedDatabaseStatus>? EncryptedDatabases { get; set; }
    public DateTime? LastBackup { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class EncryptedDatabaseStatus
{
    public string Name { get; set; } = string.Empty;
    public string? EncryptionState { get; set; }
    public string? EncryptionAlgorithm { get; set; }
}
