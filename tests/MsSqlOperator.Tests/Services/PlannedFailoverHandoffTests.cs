using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>The target holds the whole log of the previous primary, database by database.</summary>
public class PlannedFailoverHandoffTests
{
    private static AgDatabaseStateInfo Row(string db, string replica, decimal? lsn)
        => new(db, replica, "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0, LastHardenedLsn: lsn);

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 120, true)]   // the target is never ahead in practice; ahead is not a loss
    [InlineData(120, 100, false)]
    public void Compare_ByLastHardenedLsn(int onPrimary, int onTarget, bool verified)
    {
        var result = PlannedFailoverHandoff.Compare(["app"], [Row("app", "ag-0", onPrimary)], "ag-0", [Row("app", "ag-1", onTarget)], "ag-1");

        Assert.Equal(verified, result.Verified);
    }

    [Fact]
    public void Compare_APositionMissingOnEitherSide_IsNotVerified()
    {
        var unreadableOnPrimary = PlannedFailoverHandoff.Compare(["app"], [], "ag-0", [Row("app", "ag-1", 100)], "ag-1");
        var missingOnTarget = PlannedFailoverHandoff.Compare(["app"], [Row("app", "ag-0", 100)], "ag-0", [Row("app", "ag-1", null)], "ag-1");

        Assert.False(unreadableOnPrimary.Verified);
        Assert.Contains("no log position on ag-0", unreadableOnPrimary.Gap);
        Assert.False(missingOnTarget.Verified);
        Assert.Contains("no log position on ag-1", missingOnTarget.Gap);
    }

    [Fact]
    public void Compare_ReadsEachReplicaForItsOwnCopy_AndChecksEveryDatabase()
    {
        // The primary's view also lists the target's row; only the target's own answer counts.
        var result = PlannedFailoverHandoff.Compare(["app", "billing"],
            [Row("app", "ag-0", 100), Row("app", "ag-1", 100), Row("billing", "ag-0", 300)], "ag-0",
            [Row("app", "ag-1", 100), Row("billing", "ag-1", 250)], "ag-1");

        Assert.False(result.Verified);
        Assert.Contains("billing: hardened up to 250 on ag-1, 300 on ag-0", result.Gap);
        Assert.DoesNotContain("app", result.Gap);
    }
}
