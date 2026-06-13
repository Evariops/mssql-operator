using FluentAssertions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlConfigurationValidatorTests
{
    private readonly SqlConfigurationValidator _validator = new();

    [Fact]
    public void Create_ValidConfig_ReturnsSuccess()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                SpConfigure = new SpConfigureSpec
                {
                    MaxServerMemoryMb = 4096,
                    MaxDegreeOfParallelism = 4,
                    CostThresholdForParallelism = 50
                }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_MaxMemoryTooLow_ReturnsFail()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                SpConfigure = new SpConfigureSpec { MaxServerMemoryMb = 64 }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidMaxDop_ReturnsFail()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                SpConfigure = new SpConfigureSpec { MaxDegreeOfParallelism = -1 }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_MinGreaterThanMax_ReturnsFail()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                SpConfigure = new SpConfigureSpec
                {
                    MinServerMemoryMb = 8192,
                    MaxServerMemoryMb = 4096
                }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_NullSpConfigure_ReturnsSuccess()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec()
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeTrue();
    }
}
