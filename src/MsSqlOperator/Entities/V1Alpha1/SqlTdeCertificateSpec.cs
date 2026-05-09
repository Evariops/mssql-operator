using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlTdeCertificateSpec
{
    public ResourceReference? InstanceRef { get; set; }
    public ResourceReference? AvailabilityGroupRef { get; set; }
    public CertificateSpec? Certificate { get; set; }
    public List<EncryptDatabaseSpec>? EncryptDatabases { get; set; }
    public RotationSpec? Rotation { get; set; }
}

public class CertificateSpec
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = "Generate";
    public CertificateImportSpec? ImportFrom { get; set; }
    public CertificateGenerateSpec? Generate { get; set; }
    public CertificateBackupSpec? BackupToSecret { get; set; }
}

public class CertificateImportSpec
{
    public SecretReference? CertificateSecretRef { get; set; }
    public SecretReference? PrivateKeySecretRef { get; set; }
    public SecretReference? DecryptionPasswordSecretRef { get; set; }
}

public class CertificateGenerateSpec
{
    public string? Subject { get; set; }
    public string? ExpiryDate { get; set; }
    public int KeyLength { get; set; } = 2048;
}

public class CertificateBackupSpec
{
    public bool Enabled { get; set; } = true;
    public string? SecretName { get; set; }
    public SecretReference? EncryptionPasswordSecretRef { get; set; }
}

public class EncryptDatabaseSpec
{
    public string Name { get; set; } = string.Empty;
    public string EncryptionAlgorithm { get; set; } = "AES_256";
    public bool Enabled { get; set; } = true;
}

public class RotationSpec
{
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 365;
    public bool AutoRotate { get; set; }
    public int NotifyBeforeExpiryDays { get; set; } = 30;
}
