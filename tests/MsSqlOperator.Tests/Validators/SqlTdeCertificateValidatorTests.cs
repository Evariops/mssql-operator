using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlTdeCertificateValidatorTests
{
    private readonly SqlTdeCertificateValidator _validator = new();

    private static SqlTdeCertificate CreateValid() => new()
    {
        Spec = new SqlTdeCertificateSpec
        {
            Target = new TargetSelector { Instances = ["my-instance"] },
            Certificate = new CertificateSpec
            {
                LogicalName = "tde-cert",
                Source = new CertificateSourceSpec
                {
                    Type = "OperatorManaged",
                    OperatorManaged = new OperatorManagedCertificateSourceSpec
                    {
                        ExpiryDate = "2026-12-31",
                        SourceSecretEncryptionPasswordRef = new SecretReference { Name = "pwd-secret", Key = "password" }
                    }
                }
            },
            Databases = new TdeDatabaseScopeSpec
            {
                Selector = new DatabaseSelectorSpec { AllUserDatabases = true },
                Policy = new DatabaseTdePolicySpec()
            }
        }
    };

    // ── Certificate rules ──────────────────────

    [Fact]
    public void Create_ValidEntity_ReturnsSuccess()
    {
        var result = _validator.Create(CreateValid(), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_NoTarget_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Target = new TargetSelector();
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_EmptyLogicalName_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.LogicalName = "";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidSourceType_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source.Type = "Invalid";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_OperatorManagedWithoutSpec_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source.OperatorManaged = null;
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_OperatorManagedWithoutExpiryDate_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source.OperatorManaged!.ExpiryDate = "";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_KubernetesSecretValid_ReturnsSuccess()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source = new CertificateSourceSpec
        {
            Type = "KubernetesSecret",
            KubernetesSecret = new KubernetesSecretCertificateSourceSpec
            {
                Discovery = new CertificateSecretDiscoverySpec
                {
                    Names = [new CertificateSecretVersionRef { SecretName = "my-secret" }]
                }
            }
        };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_KubernetesSecretWithoutDiscovery_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source = new CertificateSourceSpec
        {
            Type = "KubernetesSecret",
            KubernetesSecret = new KubernetesSecretCertificateSourceSpec
            {
                Discovery = new CertificateSecretDiscoverySpec()
            }
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_KubernetesSecretNullSpec_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source = new CertificateSourceSpec
        {
            Type = "KubernetesSecret",
            KubernetesSecret = null
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    // Names and matchLabels mutually exclusive
    [Fact]
    public void Create_KubernetesSecretNamesAndLabels_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.Source = new CertificateSourceSpec
        {
            Type = "KubernetesSecret",
            KubernetesSecret = new KubernetesSecretCertificateSourceSpec
            {
                Discovery = new CertificateSecretDiscoverySpec
                {
                    Names = [new CertificateSecretVersionRef { SecretName = "s1" }],
                    MatchLabels = new Dictionary<string, string> { ["app"] = "x" }
                }
            }
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    // ── S3 backup rules ───────────────────────

    [Fact]
    public void Create_S3BackupValid_ReturnsSuccess()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.BackupToS3 = new CertificateBackupToS3Spec
        {
            Connection = new S3ConnectionSpec
            {
                Endpoint = "https://s3.amazonaws.com",
                Bucket = "my-bucket",
                ClusterName = "prod-cluster",
                CredentialsSecretRef = new SecretReference { Name = "s3-creds", Key = "key" }
            },
            Encryption = new BackupEncryptionSpec
            {
                Type = "AesKey",
                AesKey = new AesKeyEncryptionSpec { KeySecretRef = new SecretReference { Name = "aes-key", Key = "key" } }
            }
        };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_S3BackupMissingEndpoint_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.BackupToS3 = CreateValidS3(endpoint: "");
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_S3BackupMissingBucket_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.BackupToS3 = CreateValidS3(bucket: "");
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    //
    [Fact]
    public void Create_S3BackupMissingClusterName_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.BackupToS3 = CreateValidS3(clusterName: "");
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_S3BackupInvalidEncryptionType_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Certificate.BackupToS3 = new CertificateBackupToS3Spec
        {
            Connection = new S3ConnectionSpec
            {
                Endpoint = "https://s3.example.com",
                Bucket = "b",
                ClusterName = "c",
                CredentialsSecretRef = new SecretReference { Name = "creds", Key = "k" }
            },
            Encryption = new BackupEncryptionSpec { Type = "None" }
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    // ── Database rules ────────────────────────

    [Fact]
    public void Create_NoDatabaseSelector_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Databases.Selector = new DatabaseSelectorSpec();
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_DatabaseSelectorWithNames_ReturnsSuccess()
    {
        var entity = CreateValid();
        entity.Spec.Databases.Selector = new DatabaseSelectorSpec { Names = ["MyDB"] };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_InvalidAlgorithm_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Databases.Policy.EncryptionAlgorithm = "DES";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidNonEncryptedPolicy_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Databases.Policy.NonEncryptedDatabasePolicy = "Delete";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    //
    [Fact]
    public void Create_InvalidMissingDatabasePolicy_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Databases.Selector.MissingDatabasePolicy = "Crash";
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Theory]
    [InlineData("Ignore")]
    [InlineData("Warn")]
    [InlineData("Fail")]
    public void Create_ValidMissingDatabasePolicy_ReturnsSuccess(string policy)
    {
        var entity = CreateValid();
        entity.Spec.Databases.Selector.MissingDatabasePolicy = policy;
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    // ── Rotation rules ────────────────────────

    [Fact]
    public void Create_RotationValidProtectorOnly_ReturnsSuccess()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Mode = "ProtectorOnly", Trigger = "ManualOnly" };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_RotationInvalidMode_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Mode = "Unknown" };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    // Correct trigger values
    [Theory]
    [InlineData("ManualOnly")]
    [InlineData("ManualOrInterval")]
    [InlineData("OnSourceCandidate")]
    public void Create_RotationValidTrigger_ReturnsSuccess(string trigger)
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Trigger = trigger, IntervalDays = 30 };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_RotationInvalidTrigger_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Trigger = "Auto" };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_RotationOnNewVersion_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Trigger = "OnNewVersion" };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_RotationDekRegenerateWithoutSpec_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec
        {
            Mode = "ProtectorAndDekRegenerate",
            DekRegeneration = null
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_RotationDekRegenerateWithSpec_ReturnsSuccess()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec
        {
            Mode = "ProtectorAndDekRegenerate",
            DekRegeneration = new DekRegenerationSpec()
        };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_RotationMaxParallelZero_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { MaxParallelTargets = 0 };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    //
    [Fact]
    public void Create_ManualOrInterval_IntervalZero_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec { Trigger = "ManualOrInterval", IntervalDays = 0 };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    //
    [Fact]
    public void Create_DekRegeneration_MaxParallelZero_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec
        {
            Mode = "ProtectorAndDekRegenerate",
            DekRegeneration = new DekRegenerationSpec { MaxParallelDatabasesPerTarget = 0 }
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    //
    [Fact]
    public void Create_InvalidApprovalMode_ReturnsFail()
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec
        {
            Retirement = new RetirementPolicySpec { ApprovalMode = "Never" }
        };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("AutomaticWhenSafe")]
    public void Create_ValidApprovalMode_ReturnsSuccess(string mode)
    {
        var entity = CreateValid();
        entity.Spec.Rotation = new RotationSpec
        {
            Retirement = new RetirementPolicySpec { ApprovalMode = mode }
        };
        var result = _validator.Create(entity, false);
        Assert.True(result.Valid);
    }

    // ── Helpers ─────────────────────────────────────

    private static CertificateBackupToS3Spec CreateValidS3(
        string? endpoint = null, string? bucket = null, string? clusterName = null)
    {
        return new CertificateBackupToS3Spec
        {
            Connection = new S3ConnectionSpec
            {
                Endpoint = endpoint ?? "https://s3.example.com",
                Bucket = bucket ?? "bucket",
                ClusterName = clusterName ?? "cluster",
                CredentialsSecretRef = new SecretReference { Name = "creds", Key = "k" }
            },
            Encryption = new BackupEncryptionSpec
            {
                Type = "AesKey",
                AesKey = new AesKeyEncryptionSpec { KeySecretRef = new SecretReference { Name = "key", Key = "k" } }
            }
        };
    }
}
