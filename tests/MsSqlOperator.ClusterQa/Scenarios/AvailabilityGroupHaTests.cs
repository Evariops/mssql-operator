using System.Text.RegularExpressions;
using FluentAssertions;
using MsSqlOperator.ClusterQa.Infrastructure;

namespace MsSqlOperator.ClusterQa.Scenarios;

/// <summary>
/// Live HA scenarios against the AG brought up by <see cref="AgQaEnvironment"/>. Ordered so the
/// failover scenario runs after the topology/replication invariants are confirmed.
/// </summary>
[Collection(AgQaCollection.Name)]
public sealed class AvailabilityGroupHaTests(AgQaEnvironment env)
{
    [ClusterFact]
    public async Task Ag_Is_Running_With_Exactly_One_Writer()
    {
        var primary = await env.PrimaryAsync();
        primary.Should().NotBeNullOrEmpty("the operator must confirm a primary from the DMVs");

        // The R/W listener must route to the confirmed primary — this is the no-interruption anchor.
        var listenerTarget = await env.ListenerTargetAsync();
        listenerTarget.Should().Be(primary, "the R/W listener Endpoints must point at the confirmed primary");

        // Exactly one pod may carry the writer label, or the listener would fan out writes (split brain).
        var writers = await env.Kubectl.RunAsync(default,
            "-n", env.Namespace, "get", "pods",
            "-l", "mssql-operator.evariops.com/listener-ready=writer", "-o", "name");
        writers.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
    }

    [ClusterFact]
    public async Task Database_Is_Synchronized_On_Both_Replicas()
    {
        var primary = await env.PrimaryAsync();
        var secondary = await OtherReplicaAsync(primary);

        // A fresh row on the primary must appear on the readable secondary (real replication, not a cache).
        var sentinel = Guid.NewGuid().ToString("N");
        await env.ScalarAsync(primary, $"INSERT dbo.Customers(Name, Email) VALUES (N'qa-{sentinel}', N'{sentinel}@qa');");

        string onSecondary = "";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            onSecondary = await env.ScalarAsync(secondary, $"SELECT COUNT(*) FROM dbo.Customers WHERE Email = N'{sentinel}@qa';");
            if (onSecondary == "1") break;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        onSecondary.Should().Be("1", "a row committed on the primary must replicate to the synchronous secondary");
    }

    [ClusterFact]
    public async Task Planned_Failover_Repoints_Listener_With_No_Data_Loss_And_Bounded_Gap()
    {
        var oldPrimary = await env.PrimaryAsync();
        var targetOrdinal = 1 - OrdinalOf(oldPrimary);          // 2-replica AG: flip to the other ordinal
        var newExpected = $"{AgQaEnvironment.AgName}-{targetOrdinal}";

        await env.FailoverToAsync(targetOrdinal);

        // 1. The operator must confirm the role flip and repoint the listener.
        var flipped = await WaitUntilAsync(async () => await env.PrimaryAsync() == newExpected, TimeSpan.FromSeconds(120));
        flipped.Should().BeTrue($"the operator should promote {newExpected} after the planned failover");
        (await env.ListenerTargetAsync()).Should().Be(newExpected, "the R/W listener must follow the new primary");

        // Let the probe accumulate writes on the new primary before we read the handoff back.
        await Task.Delay(TimeSpan.FromSeconds(5));

        // 2. No committed write may be lost (synchronous commit ⇒ zero data loss). Every Seq the probe
        //    logged as committed (OK) must be present in the table that the new primary now serves.
        var okSeqs = ParseCommittedSeqs(await env.ProbeLogsAsync());
        okSeqs.Should().NotBeEmpty("the probe must have committed writes through the listener");
        var missing = await CountMissingAsync(newExpected, okSeqs);
        missing.Should().Be(0, "a synchronous-commit planned failover must not lose any committed write");

        // 3. The write interruption (gap at the writer handoff in the probe data) must be bounded.
        var gapSeconds = await HandoffGapSecondsAsync(newExpected);
        gapSeconds.Should().BeLessThanOrEqualTo(QaConfig.MaxPlannedFailoverGapSeconds,
            "the listener should repoint within the allowed interruption window");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<string> OtherReplicaAsync(string replica)
    {
        var ordinal = 1 - OrdinalOf(replica);
        return $"{AgQaEnvironment.AgName}-{ordinal}";
    }

    private static int OrdinalOf(string podName)
    {
        var dash = podName.LastIndexOf('-');
        return dash >= 0 && int.TryParse(podName[(dash + 1)..], out var n) ? n : 0;
    }

    private static IReadOnlyList<int> ParseCommittedSeqs(string probeLogs) =>
        Regex.Matches(probeLogs, @"^OK (\d+)", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

    /// <summary>How many of the probe's committed Seqs are absent from the table on <paramref name="pod"/>.</summary>
    private async Task<int> CountMissingAsync(string pod, IReadOnlyList<int> okSeqs)
    {
        var min = okSeqs.Min();
        var max = okSeqs.Max();
        var present = await env.ScalarAsync(pod,
            $"SELECT COUNT(*) FROM dbo.HaProbe WHERE Seq BETWEEN {min} AND {max};");
        // The probe skips Seqs it failed to commit, so compare against the OK set, not the range.
        return okSeqs.Count - int.Parse(present.Trim());
    }

    /// <summary>The time gap (seconds) at the writer handoff, from the probe rows on the new primary.</summary>
    private async Task<double> HandoffGapSecondsAsync(string pod)
    {
        var ms = await env.ScalarAsync(pod, """
            WITH x AS (SELECT Seq, Ts, Writer, LAG(Ts) OVER (ORDER BY Seq) prevTs,
                              LAG(Writer) OVER (ORDER BY Seq) prevW FROM dbo.HaProbe)
            SELECT ISNULL(MAX(DATEDIFF(MILLISECOND, prevTs, Ts)), 0) FROM x WHERE Writer <> prevW;
            """);
        return int.Parse(ms.Trim()) / 1000.0;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return false;
    }
}
