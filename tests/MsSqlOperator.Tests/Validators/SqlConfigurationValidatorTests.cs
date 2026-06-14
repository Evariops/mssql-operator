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

        Assert.True(result.Valid);
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

        Assert.False(result.Valid);
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

        Assert.False(result.Valid);
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

        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_NullSpConfigure_ReturnsSuccess()
    {
        var entity = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec()
        };

        var result = _validator.Create(entity, false);

        Assert.True(result.Valid);
    }

    // ── Dangerous-option opt-in (advanced[] and typed toggles) ──────────────

    private static SqlConfiguration WithSpConfigure(SpConfigureSpec sp) =>
        new() { Spec = new SqlConfigurationSpec { SpConfigure = sp } };

    [Fact]
    public void Create_AdvancedDangerous_WithoutAllowDangerous_ReturnsFail()
    {
        var entity = WithSpConfigure(new SpConfigureSpec
        {
            Advanced = [new AdvancedConfigParam { Name = "xp_cmdshell", Value = 1 }],
        });

        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_AdvancedDangerous_WithAllowDangerous_ReturnsSuccess()
    {
        var entity = WithSpConfigure(new SpConfigureSpec
        {
            Advanced = [new AdvancedConfigParam { Name = "xp_cmdshell", Value = 1, AllowDangerous = true }],
        });

        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_AdvancedUnknownOption_ReturnsFail()
    {
        var entity = WithSpConfigure(new SpConfigureSpec
        {
            Advanced = [new AdvancedConfigParam { Name = "totally made up option", Value = 1, AllowDangerous = true }],
        });

        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_TypedDangerous_WithoutAllowDangerous_ReturnsFail()
    {
        Assert.False(_validator.Create(WithSpConfigure(new SpConfigureSpec { ClrEnabled = true }), false).Valid);
        Assert.False(_validator.Create(WithSpConfigure(new SpConfigureSpec { CrossDbOwnershipChaining = true }), false).Valid);
        Assert.False(_validator.Create(WithSpConfigure(new SpConfigureSpec { RemoteAdminConnections = true }), false).Valid);
    }

    [Fact]
    public void Create_TypedDangerous_WithAllowDangerous_ReturnsSuccess() =>
        Assert.True(_validator.Create(WithSpConfigure(new SpConfigureSpec { ClrEnabled = true, AllowDangerous = true }), false)
            .Valid);

    [Fact]
    public void Create_TypedDangerous_Disabling_NeedsNoOptIn()
    {
        // Turning a dangerous option OFF must never require the opt-in.
        Assert.True(_validator.Create(WithSpConfigure(new SpConfigureSpec { ClrEnabled = false }), false).Valid);
    }
}
