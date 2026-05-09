using FluentAssertions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlAvailabilityGroupValidatorTests
{
    private readonly SqlAvailabilityGroupValidator _validator = new();

    [Fact]
    public void Create_ValidAg_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroup
        {
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = 3,
                SynchronousReplicas = 2,
                InstanceTemplate = new AgInstanceTemplateSpec
                {
                    Version = "2022",
                    Edition = "Enterprise"
                }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_StandardEdition_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup
        {
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = 3,
                SynchronousReplicas = 2,
                InstanceTemplate = new AgInstanceTemplateSpec
                {
                    Version = "2022",
                    Edition = "Standard"
                }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_TooFewReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup
        {
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = 1,
                SynchronousReplicas = 1,
                InstanceTemplate = new AgInstanceTemplateSpec { Edition = "Enterprise" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_TooManyReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup
        {
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = 10,
                SynchronousReplicas = 2,
                InstanceTemplate = new AgInstanceTemplateSpec { Edition = "Enterprise" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_SyncReplicasExceedsReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup
        {
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = 3,
                SynchronousReplicas = 5,
                InstanceTemplate = new AgInstanceTemplateSpec { Edition = "Enterprise" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }
}
