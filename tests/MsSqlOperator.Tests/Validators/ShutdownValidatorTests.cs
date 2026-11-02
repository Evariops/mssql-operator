using MsSqlOperator.Entities.Common;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

/// <summary>The bounds of the shutdown spec.</summary>
public class ShutdownValidatorTests
{
    [Theory]
    [InlineData(180, 30, true)]
    [InlineData(180, 0, true)]
    [InlineData(180, 300, true)]
    [InlineData(180, -1, false)]
    [InlineData(180, 301, false)]
    [InlineData(30, 30, false)]
    public void TheGracePeriodAndTheHandoffWait_AreBounded(long grace, int handoff, bool valid)
        => Assert.Equal(valid, ShutdownValidator.Validate(new ShutdownSpec { GracePeriodSeconds = grace, PrimaryHandoffSeconds = handoff }) is null);
}
