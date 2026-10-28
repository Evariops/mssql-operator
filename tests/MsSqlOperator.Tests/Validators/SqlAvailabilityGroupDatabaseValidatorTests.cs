using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlAvailabilityGroupDatabaseValidatorTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly SqlAvailabilityGroupDatabaseValidator _validator;

    public SqlAvailabilityGroupDatabaseValidatorTests()
    {
        _validator = new SqlAvailabilityGroupDatabaseValidator(_k8s, NullLogger<SqlAvailabilityGroupDatabaseValidator>.Instance);
    }

    private static SqlAvailabilityGroupDatabaseSpec CreateValidSpec() => new()
    {
        AvailabilityGroupRef = new ResourceReference { Name = "orders-ag" },
        DatabaseName = "OrdersDB",
        SeedingMode = "Automatic"
    };

    // Use internal Validate() for structural tests (no cross-CRD)
    [Fact]
    public void Validate_ValidSpec_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroupDatabase { Spec = CreateValidSpec() };
        Assert.True(_validator.ValidateSpec(entity).Valid);
    }

    [Theory]
    [InlineData("Auto", 50, 24, true)]       // deprecated, still accepted
    [InlineData(null, 50, null, true)]
    [InlineData("Local", 50, null, true)]
    [InlineData("Disabled", 50, 24, true)]
    [InlineData("ObjectStorage", 50, 24, false)] // not available yet
    [InlineData("Sometimes", 50, 24, false)]
    [InlineData("Local", 5, 24, false)]
    [InlineData("Local", 95, 24, false)]
    [InlineData("Local", 50, 0, false)]
    public void Validate_LogBackup(string? mode, int threshold, int? retention, bool valid)
    {
        //
        var spec = CreateValidSpec();
        spec.LogBackup = new AgDatabaseLogBackupSpec { Mode = mode, ThresholdPercent = threshold, RetentionHours = retention };
        Assert.Equal(valid, _validator.ValidateSpec(new SqlAvailabilityGroupDatabase { Spec = spec }).Valid);
    }

    [Fact]
    public void Validate_ManualSeeding_ReturnsSuccess()
    {
        var spec = CreateValidSpec();
        spec.SeedingMode = "Manual";
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.True(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Validate_MissingAgRef_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.AvailabilityGroupRef = new ResourceReference { Name = "" };
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.False(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Validate_MissingDatabaseName_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.DatabaseName = "";
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.False(_validator.ValidateSpec(entity).Valid);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("msdb")]
    [InlineData("model")]
    [InlineData("tempdb")]
    public void Validate_SystemDatabase_ReturnsFail(string dbName)
    {
        var spec = CreateValidSpec();
        spec.DatabaseName = dbName;
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.False(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Validate_InvalidDatabaseName_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.DatabaseName = "invalid-name-with-dashes";
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.False(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Validate_InvalidSeedingMode_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.SeedingMode = "Invalid";
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.False(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Validate_WithDataMovement_ReturnsSuccess()
    {
        var spec = CreateValidSpec();
        spec.DataMovement = new AgDatabaseDataMovementSpec { Suspended = false, RequireManualResumeAfterDataLoss = true };
        var entity = new SqlAvailabilityGroupDatabase { Spec = spec };
        Assert.True(_validator.ValidateSpec(entity).Valid);
    }

    [Fact]
    public void Update_ChangingAgRef_ReturnsFail()
    {
        var oldSpec = CreateValidSpec();
        var newSpec = CreateValidSpec();
        newSpec.AvailabilityGroupRef = new ResourceReference { Name = "other-ag" };

        var oldEntity = new SqlAvailabilityGroupDatabase { Spec = oldSpec };
        var newEntity = new SqlAvailabilityGroupDatabase { Spec = newSpec };
        Assert.False(_validator.Update(oldEntity, newEntity, false).Valid);
    }

    [Fact]
    public void Update_ChangingDatabaseName_ReturnsFail()
    {
        var oldSpec = CreateValidSpec();
        var newSpec = CreateValidSpec();
        newSpec.DatabaseName = "DifferentDB";

        var oldEntity = new SqlAvailabilityGroupDatabase { Spec = oldSpec };
        var newEntity = new SqlAvailabilityGroupDatabase { Spec = newSpec };
        Assert.False(_validator.Update(oldEntity, newEntity, false).Valid);
    }

    [Fact]
    public void Update_ChangingSeedingMode_ReturnsSuccess()
    {
        var oldSpec = CreateValidSpec();
        var newSpec = CreateValidSpec();
        newSpec.SeedingMode = "Manual";

        var oldEntity = new SqlAvailabilityGroupDatabase { Spec = oldSpec };
        var newEntity = new SqlAvailabilityGroupDatabase { Spec = newSpec };
        Assert.True(_validator.Update(oldEntity, newEntity, false).Valid);
    }

    [Fact]
    public void Update_ChangingDataMovement_ReturnsSuccess()
    {
        var oldSpec = CreateValidSpec();
        var newSpec = CreateValidSpec();
        newSpec.DataMovement = new AgDatabaseDataMovementSpec { Suspended = true };

        var oldEntity = new SqlAvailabilityGroupDatabase { Spec = oldSpec };
        var newEntity = new SqlAvailabilityGroupDatabase { Spec = newSpec };
        Assert.True(_validator.Update(oldEntity, newEntity, false).Valid);
    }
}
