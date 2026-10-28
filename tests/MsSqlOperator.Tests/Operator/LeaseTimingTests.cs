using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Breakout;

namespace MsSqlOperator.Tests.Operator;

public class LeaseTimingTests
{
    [Fact]
    public void Defaults_LeaveALeaderTimeToExitBeforeAnotherTakesTheLease()
    {
        // 15 s / 10 s / 2 s with 5 s to exit let two leaders overlap by 2 s.
        new OperatorOptions().EnsureSafeLeaseTiming();
        new BreakoutOptions().EnsureSafeLeaseTiming();
    }

    [Theory]
    [InlineData(15, 10, 2, 5)] // the former defaults
    [InlineData(30, 20, 5, 5)] // exactly the lease duration: still unsafe
    public void EnsureSafe_NoMargin_Refused(int lease, int renew, int retry, int exit)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LeaseTiming.EnsureSafe("leader",
            TimeSpan.FromSeconds(lease), TimeSpan.FromSeconds(renew), TimeSpan.FromSeconds(retry), TimeSpan.FromSeconds(exit)));
        Assert.Contains("must stay below the lease duration", ex.Message);
    }

    [Fact]
    public void BreakoutOptions_FormerDefaults_Refused()
    {
        var options = new BreakoutOptions { LeaseDurationSeconds = 15, LeaseRenewDeadlineSeconds = 10 };

        Assert.Throws<InvalidOperationException>(options.EnsureSafeLeaseTiming);
    }
}
