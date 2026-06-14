using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

/// <summary>Acceptance tests for the validator hardening: immutable fields, the sp_configure allowlist, TDE settings.</summary>
public class ValidatorHardeningTests
{
    // ── Immutability at admission ──────────────────────

    private static SqlInstance Instance(string mode = "SingleNode", string? storageClass = "fast", string size = "10Gi") => new()
    {
        Spec = new SqlInstanceSpec
        {
            Mode = mode,
            Version = "2022",
            Edition = "Developer",
            Memory = new MemorySpec { Total = "4Gi" },
            Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = size, StorageClassName = storageClass } }
        }
    };

    [Fact]
    public void SqlInstance_ModeChange_Rejected()
    {
        var v = new SqlInstanceValidator();
        var result = v.Update(Instance("SingleNode"), Instance("AvailabilityGroupMember"), false);
        Assert.False(result.Valid);
        Assert.Contains("spec.mode is immutable", result.Message);
    }

    [Fact]
    public void SqlInstance_StorageClassChange_Rejected()
    {
        var v = new SqlInstanceValidator();
        var result = v.Update(Instance(storageClass: "fast"), Instance(storageClass: "slow"), false);
        Assert.False(result.Valid);
        Assert.Contains("storageClassName is immutable", result.Message);
    }

    [Fact]
    public void SqlInstance_StorageShrink_Rejected()
    {
        var v = new SqlInstanceValidator();
        var result = v.Update(Instance(size: "20Gi"), Instance(size: "10Gi"), false);
        Assert.False(result.Valid);
        Assert.Contains("cannot shrink", result.Message);
    }

    [Fact]
    public void SqlInstance_StorageGrowth_Allowed()
    {
        // Growth is orchestrated, not rejected.
        var v = new SqlInstanceValidator();
        var result = v.Update(Instance(size: "10Gi"), Instance(size: "20Gi"), false);
        Assert.True(result.Valid);
    }

    // ── sp_configure allowlist ─────────────────────────

    private static SqlConfiguration Config(string advName, bool allowDangerous = false) => new()
    {
        Spec = new SqlConfigurationSpec
        {
            SpConfigure = new SpConfigureSpec
            {
                Advanced = [new AdvancedConfigParam { Name = advName, Value = 1, AllowDangerous = allowDangerous }]
            }
        }
    };

    [Fact]
    public void SpConfigure_SafeOption_Allowed()
        => Assert.True(new SqlConfigurationValidator().Create(Config("optimize for ad hoc workloads"), false).Valid);

    [Fact]
    public void SpConfigure_DangerousOption_RejectedWithoutOptIn()
    {
        var result = new SqlConfigurationValidator().Create(Config("xp_cmdshell"), false);
        Assert.False(result.Valid);
        Assert.Contains("allowDangerous", result.Message);
    }

    [Fact]
    public void SpConfigure_DangerousOption_AllowedWithOptIn()
        => Assert.True(new SqlConfigurationValidator().Create(Config("xp_cmdshell", allowDangerous: true), false).Valid);

    [Fact]
    public void SpConfigure_UnknownOption_Rejected()
    {
        var result = new SqlConfigurationValidator().Create(Config("some random knob"), false);
        Assert.False(result.Valid);
        Assert.Contains("not in the allowlist", result.Message);
    }

    // ── TDE strict facets ──────────────────────────────

    private static SqlTdeCertificate Tde(Action<SqlTdeCertificateSpec>? cfg = null)
    {
        var spec = new SqlTdeCertificateSpec
        {
            Target = new TargetSelector { Instances = ["i"] },
            Certificate = new CertificateSpec
            {
                LogicalName = "tde-cert",
                Source = new CertificateSourceSpec
                {
                    Type = "OperatorManaged",
                    OperatorManaged = new OperatorManagedCertificateSourceSpec { ExpiryDate = "2030-01-01", KeyLength = 2048 }
                }
            }
        };
        cfg?.Invoke(spec);
        return new SqlTdeCertificate { Spec = spec };
    }

    [Fact]
    public void Tde_InvalidExpiryDate_Rejected()
    {
        var result = new SqlTdeCertificateValidator().Create(
            Tde(s => s.Certificate.Source.OperatorManaged!.ExpiryDate = "banana"), false);
        Assert.False(result.Valid);
        Assert.Contains("ISO 8601", result.Message);
    }

    [Fact]
    public void Tde_InvalidKeyLength_Rejected()
    {
        var result = new SqlTdeCertificateValidator().Create(
            Tde(s => s.Certificate.Source.OperatorManaged!.KeyLength = 1024), false);
        Assert.False(result.Valid);
        Assert.Contains("keyLength", result.Message);
    }

    [Fact]
    public void Tde_NonDns1123LogicalName_Rejected()
    {
        var result = new SqlTdeCertificateValidator().Create(Tde(s => s.Certificate.LogicalName = "Tde_Cert"), false);
        Assert.False(result.Valid);
        Assert.Contains("DNS-1123", result.Message);
    }

    [Fact]
    public void Tde_LogicalNameChange_Rejected()
    {
        var v = new SqlTdeCertificateValidator();
        var result = v.Update(Tde(), Tde(s => s.Certificate.LogicalName = "other-cert"), false);
        Assert.False(result.Valid);
        Assert.Contains("logicalName is immutable", result.Message);
    }

    // ── Memory quantity parsing ───────────────────────

    [Theory]
    [InlineData("16G", 15258)]    // 16e9 / 1024^2
    [InlineData("16Gi", 16384)]
    [InlineData("1024Mi", 1024)]
    public void MemoryCalculator_ParsesKubernetesQuantities(string quantity, int expectedMb)
        => Assert.Equal(expectedMb, MemoryCalculator.ParseToMb(quantity));

    [Fact]
    public void MemoryCalculator_RejectsGarbage()
        => Assert.Throws<ArgumentException>(() => MemoryCalculator.ParseToMb("not-a-size"));

    // ── Stored procedure source rules ──────────────────

    [Fact]
    public void StoredProc_InlineAndGithub_Rejected()
    {
        var entity = new SqlStoredProcedure
        {
            Spec = new SqlStoredProcedureSpec
            {
                Target = new TargetSelector { Instances = ["i"] },
                Inline = [new InlineProcedureSpec { Name = "p", Schema = "dbo", Definition = "CREATE PROC p AS SELECT 1" }],
                GitHub = new GitHubSourceSpec { Repository = "o/r" }
            }
        };
        var result = new SqlStoredProcedureValidator().Create(entity, false);
        Assert.False(result.Valid);
        Assert.Contains("mutually exclusive", result.Message);
    }

    [Fact]
    public void StoredProc_ShortSha_Rejected()
    {
        var entity = new SqlStoredProcedure
        {
            Spec = new SqlStoredProcedureSpec
            {
                Target = new TargetSelector { Instances = ["i"] },
                GitHub = new GitHubSourceSpec { Repository = "o/r", Ref = new GitHubRefSpec { Sha = "abc123" } }
            }
        };
        var result = new SqlStoredProcedureValidator().Create(entity, false);
        Assert.False(result.Valid);
        Assert.Contains("40-hex", result.Message);
    }
}
