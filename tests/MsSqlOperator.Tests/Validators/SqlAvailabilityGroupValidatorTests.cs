using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlAvailabilityGroupValidatorTests
{
    private readonly SqlAvailabilityGroupValidator _validator = new();

    private static SqlAvailabilityGroupSpec CreateValidSpec(string edition = "Enterprise", int count = 3, int syncCount = 2) => new()
    {
        Edition = edition,
        Replicas = new AgReplicaTopologySpec { Count = count, SynchronousCount = syncCount },
        SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
        MasterKeyPasswordSecretRef = new SecretReference { Name = "mk-secret", Key = "password" },
        HadrCertificate = new AgHadrCertificateSpec { Source = "Generate", SecretName = "cert" },
        InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022" }
    };

    [Fact]
    public void Create_ValidEnterprise_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec() };
        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_ValidStandard_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Standard", 2, 2) };
        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_HadrCertificateImportSource_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.HadrCertificate!.Source = "Import";
        Assert.False(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_HadrCertificateUnsupportedModel_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.HadrCertificate!.Model = "Shared";
        Assert.False(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_StandardWithThreeReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Standard", 3, 2) };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_StandardWithReadOnlyListener_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.ReadOnlyListener = new AgListenerSpec { Port = 1434 };
        var entity = new SqlAvailabilityGroup { Spec = spec };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_StandardWithReadOnlyRouting_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.ReadOnlyRouting = new AgReadOnlyRoutingSpec { Enabled = true };
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_StandardWithBackupPreference_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.BackupPreference = "PRIMARY";
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_EnterpriseTooManyReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 10, 2) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_TooFewReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 1, 1) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_SyncCountExceedsReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 3, 5) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_InvalidEdition_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Express") };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    // Database validation moved to SqlAvailabilityGroupDatabaseValidatorTests

    [Fact]
    public void Create_RequiredSyncSecondariesEqualsSyncCount_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.Replicas.RequiredSynchronizedSecondariesToCommit = 2; // equals SynchronousCount
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }
}
