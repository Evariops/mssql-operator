using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlTdeCertificateSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public MsSqlOperator.Entities.Common.AutomationSpec? Automation { get; set; }

    public TargetSelector Target { get; set; } = new();
    public CertificateSpec Certificate { get; set; } = new();
    public TdeDatabaseScopeSpec Databases { get; set; } = new();
    public RotationSpec? Rotation { get; set; }
}

public class CertificateSpec
{
    [MsSqlOperator.Operator.Abstractions.Immutable("certificate.logicalName is immutable: it is the identity of the version/secret chain")]
    public string LogicalName { get; set; } = string.Empty;
    public CertificateSourceSpec Source { get; set; } = new();
    public CertificateArchiveDestinationSpec? ArchiveToSecret { get; set; }
    public CertificateBackupToS3Spec? BackupToS3 { get; set; }
}

public class CertificateSourceSpec
{
    public string Type { get; set; } = "OperatorManaged";
    public OperatorManagedCertificateSourceSpec? OperatorManaged { get; set; }
    public KubernetesSecretCertificateSourceSpec? KubernetesSecret { get; set; }
}

public class OperatorManagedCertificateSourceSpec
{
    public string SubjectTemplate { get; set; } = "TDE certificate {logicalName}";
    public string ExpiryDate { get; set; } = string.Empty;
    public int KeyLength { get; set; } = 2048;
    public string SqlCertificateNameTemplate { get; set; } = "{logicalName}_{version}";
    public string SourceSecretNameTemplate { get; set; } = "{logicalName}-{version}";
    public SecretReference SourceSecretEncryptionPasswordRef { get; set; } = new();
}

public class KubernetesSecretCertificateSourceSpec
{
    public CertificateSecretDiscoverySpec Discovery { get; set; } = new();
    public string ActivationPolicy { get; set; } = "SecretRole";
}

public class CertificateSecretDiscoverySpec
{
    public string? Namespace { get; set; }
    public List<CertificateSecretVersionRef>? Names { get; set; }
    public Dictionary<string, string>? MatchLabels { get; set; }
}

public class CertificateSecretVersionRef
{
    public string SecretName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? SqlCertificateName { get; set; }
    public string CertificateKey { get; set; } = "certificate.cer";
    public string PrivateKeyKey { get; set; } = "private-key.pvk";
    public string? PasswordKey { get; set; } = "password";
}

public class CertificateArchiveDestinationSpec
{
    public string SecretNameTemplate { get; set; } = "{logicalName}-{thumbprintShort}-{timestamp}";
    public SecretReference EncryptionPasswordSecretRef { get; set; } = new();
    public bool Immutable { get; set; } = true;
}

public class CertificateBackupToS3Spec
{
    public S3ConnectionSpec Connection { get; set; } = new();
    public string ObjectKeyTemplate { get; set; } = "{clusterName}/tde/{logicalName}/{version}/{thumbprintShort}.enc";
    public BackupEncryptionSpec Encryption { get; set; } = new();
}

public class S3ConnectionSpec
{
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string ClusterName { get; set; } = string.Empty;
    public SecretReference CredentialsSecretRef { get; set; } = new();
    public bool ForcePathStyle { get; set; }
}

public class BackupEncryptionSpec
{
    public string Type { get; set; } = "AesKey";
    public AesKeyEncryptionSpec? AesKey { get; set; }
    public EnvelopeEncryptionSpec? EnvelopeEncryption { get; set; }
}

public class AesKeyEncryptionSpec
{
    public SecretReference KeySecretRef { get; set; } = new();
}

public class EnvelopeEncryptionSpec
{
    public SecretReference PublicKeySecretRef { get; set; } = new();
    public SecretReference? PrivateKeySecretRef { get; set; }
}

public class TdeDatabaseScopeSpec
{
    public DatabaseSelectorSpec Selector { get; set; } = new();
    public DatabaseTdePolicySpec Policy { get; set; } = new();
}

public class DatabaseSelectorSpec
{
    public bool AllUserDatabases { get; set; }
    public List<string>? Names { get; set; }
    public List<string>? NamePatterns { get; set; }
    public List<string>? ExcludeNames { get; set; }
    public List<string>? ExcludePatterns { get; set; }
    public bool ExcludeSystemDatabases { get; set; } = true;
    public string MissingDatabasePolicy { get; set; } = "Warn";
}

public class DatabaseTdePolicySpec
{
    public string EncryptionAlgorithm { get; set; } = "AES_256";
    public string NonEncryptedDatabasePolicy { get; set; } = "Encrypt";
    /// <summary>Encryption scans (SET ENCRYPTION ON, DEK regeneration, decryption) running
    /// at once on one server. A scan reads and rewrites the whole database: several at once saturate
    /// the I/O of the instance. The next database waits for a later pass.</summary>
    public int MaxConcurrentEncryptionScans { get; set; } = 1;
}

public class RotationSpec
{
    public int IntervalDays { get; set; } = 365;
    public int AlertAfterCertificateAgeDays { get; set; } = 730;
    public string Mode { get; set; } = "ProtectorOnly";
    public string Trigger { get; set; } = "ManualOnly";
    public int MaxParallelTargets { get; set; } = 1;
    public DekRegenerationSpec? DekRegeneration { get; set; }
    public RetirementPolicySpec Retirement { get; set; } = new();
}

public class DekRegenerationSpec
{
    public int MaxParallelDatabasesPerTarget { get; set; } = 1;
    /// <summary>Deprecated, ignored — the regeneration statement returns before the scan it
    /// starts, so the timeout never applied to anything. The scans are bounded by
    /// <c>databases.policy.maxConcurrentEncryptionScans</c>.</summary>
    [System.Obsolete("Ignored: the scan runs after the statement returns; see maxConcurrentEncryptionScans.")]
    public int PerDatabaseTimeoutMinutes { get; set; } = 120;
}

public class RetirementPolicySpec
{
    public int NotBeforeDays { get; set; } = 90;
    public string ApprovalMode { get; set; } = "Manual";
    public bool RequireFreshFullBackupAfterRotation { get; set; } = true;
    public bool RequireLogBackupAfterDekRegeneration { get; set; } = true;
}
