using System.Text.RegularExpressions;
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
        Assert.False(string.IsNullOrEmpty(primary));

        // The R/W listener must route to the confirmed primary — this is the no-interruption anchor.
        var listenerTarget = await env.ListenerTargetAsync();
        Assert.True(listenerTarget == primary, "the R/W listener Endpoints must point at the confirmed primary");

        // Exactly one pod may carry the writer label, or the listener would fan out writes (split brain).
        var writers = await env.Kubectl.RunAsync(default,
            "-n", env.Namespace, "get", "pods",
            "-l", "mssql-operator.evariops.com/listener-ready=writer", "-o", "name");
        Assert.Single(writers.Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
        Assert.True(onSecondary == "1", "a row committed on the primary must replicate to the synchronous secondary");
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
        Assert.True(flipped);
        Assert.True((await env.ListenerTargetAsync()) == newExpected, "the R/W listener must follow the new primary");

        // Let the probe accumulate writes on the new primary before we read the handoff back.
        await Task.Delay(TimeSpan.FromSeconds(5));

        // 2. No committed write may be lost (synchronous commit ⇒ zero data loss). Every Seq the probe
        //    logged as committed (OK) must be present in the table that the new primary now serves.
        var okSeqs = ParseCommittedSeqs(await env.ProbeLogsAsync());
        Assert.NotEmpty(okSeqs);
        var missing = await MissingAsync(newExpected, okSeqs);
        // A failure here must be diagnosable after the namespace is gone: which writes, and whether
        // the logs mixed several probe pods (their Seqs overlap).
        var probePods = (await env.Kubectl.RunAsync(default, "-n", env.Namespace, "get", "pods", "-l", "app=ha-probe", "-o", "name"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(missing.Count == 0,
            $"a synchronous-commit planned failover must not lose any committed write: {missing.Count} missing " +
            $"({string.Join(",", missing.Take(20))}); {okSeqs.Count} acknowledged ({okSeqs.Distinct().Count()} distinct); " +
            $"{probePods} probe pod(s); failover {oldPrimary} -> {newExpected}");

        // 3. The write interruption (gap at the writer handoff in the probe data) must be bounded.
        var gapSeconds = await HandoffGapSecondsAsync(newExpected);
        Assert.True(gapSeconds <= QaConfig.MaxPlannedFailoverGapSeconds,
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

    /// <summary>The probe's committed Seqs absent from the table on <paramref name="pod"/>. Compared as
    /// sets: a count over the range was thrown off by writes the probe saw fail but that committed, and
    /// by Seqs repeated across probe pods.</summary>
    private async Task<IReadOnlyList<int>> MissingAsync(string pod, IReadOnlyList<int> okSeqs)
    {
        var rows = await env.ScalarAsync(pod,
            $"SELECT Seq FROM dbo.HaProbe WHERE Seq BETWEEN {okSeqs.Min()} AND {okSeqs.Max()};");
        var present = rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToHashSet();
        return okSeqs.Distinct().Where(s => !present.Contains(s)).Order().ToList();
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
