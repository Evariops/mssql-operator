using System.Text.Json;
using MsSqlOperator.Services.Breakout;

namespace MsSqlOperator.Tests.Services;

/// <summary>The pods in Terminating, kept from the watch.</summary>
public class TerminatingPodTrackerTests
{
    private static JsonElement Pod(string uid, bool terminating)
        => JsonDocument.Parse("{\"metadata\":{\"name\":\"" + uid + "\",\"namespace\":\"db\",\"uid\":\"" + uid + "\"" +
                              (terminating ? ",\"deletionTimestamp\":\"2026-10-28T10:00:00Z\"" : "") + "}}").RootElement;

    [Fact]
    public async Task APodEnteringTerminating_IsKept_AndWakesTheLoop()
    {
        var tracker = new TerminatingPodTracker(1);
        tracker.Relist("instance", [Pod("a", false)]);
        await tracker.WaitAsync(TimeSpan.Zero, TestContext.Current.CancellationToken); // the relist signal

        tracker.Update("instance", "MODIFIED", Pod("a", true));

        Assert.True(await tracker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("a", Assert.Single(tracker.Snapshot()).Metadata.Uid);
    }

    [Fact]
    public void ADeletedPod_IsForgotten_NotSeededUntilEverySelectorListed()
    {
        var tracker = new TerminatingPodTracker(2);
        tracker.Relist("instance", [Pod("a", true)]);
        Assert.False(tracker.Seeded);
        tracker.Relist("ag", [Pod("b", true)]);
        Assert.True(tracker.Seeded);

        tracker.Update("instance", "DELETED", Pod("a", true));

        Assert.Equal("b", Assert.Single(tracker.Snapshot()).Metadata.Uid);
    }
}
