using MsSqlOperator.Tests.Fakes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

/// <summary>An API server that does not answer makes the guard fail open within its budget.</summary>
public class CrossKindNameGuardBudgetTests
{
    [Fact]
    public async Task ApiThatNeverAnswers_FailsOpenWithinTheBudget()
    {
        var client = new FakeKubernetesClient { GetHangs = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var conflict = await CrossKindNameGuard.ConflictAsync(client, "SqlAvailabilityGroup", "sales", "db", TestContext.Current.CancellationToken);

        Assert.Null(conflict);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
    }
}
