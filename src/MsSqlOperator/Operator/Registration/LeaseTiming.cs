namespace MsSqlOperator.Operator.Registration;

/// <summary>
/// A leader that loses its Lease must have stopped acting before anyone else can take it.
/// It notices the loss at the latest <c>renewDeadline + retryPeriod</c> after its last renewal, then
/// needs <c>exitDelay</c> to stop; another candidate can acquire <c>leaseDuration</c> after that
/// renewal. With the former defaults (15 s / 10 s / 2 s, 5 s to exit) the margin was negative: two
/// writers could overlap by 2 s.
/// </summary>
public static class LeaseTiming
{
    /// <summary>Throws when <paramref name="exitDelay"/> + <paramref name="renewDeadline"/> +
    /// <paramref name="retryPeriod"/> does not fit strictly within <paramref name="leaseDuration"/>.</summary>
    public static void EnsureSafe(string lease, TimeSpan leaseDuration, TimeSpan renewDeadline, TimeSpan retryPeriod, TimeSpan exitDelay)
    {
        var worst = exitDelay + renewDeadline + retryPeriod;
        if (worst >= leaseDuration)
            throw new InvalidOperationException(
                $"Lease {lease}: exit delay ({exitDelay.TotalSeconds} s) + renew deadline ({renewDeadline.TotalSeconds} s) + " +
                $"retry period ({retryPeriod.TotalSeconds} s) = {worst.TotalSeconds} s must stay below the lease duration " +
                $"({leaseDuration.TotalSeconds} s), or a leader that lost the Lease may still act once another has taken it.");
    }
}
