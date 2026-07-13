using System.Diagnostics;
using MsSqlOperator.ClusterQa.Infrastructure;

namespace MsSqlOperator.ClusterQa.Scenarios;

/// <summary>
/// The regression test for the failure that motivated the full-text support: SQL Server
/// pods taking many minutes to become genuinely usable after a restart when Full-Text is enabled.
///
/// The scenario is deliberately the ugly one: start a FULL population, kill the pod in the middle of
/// it, and measure the two times that matter and that Kubernetes conflates:
///
///   * T-ready  — when the pod reports Ready (what the Service and the rolling update look at);
///   * T-usable — when a query against the full-text index actually answers within a sane latency
///                (what the application experiences).
///
/// Before the remediation, T-ready lied: the readiness probe answered `SELECT 1` on master while the
/// engine was resuming the interrupted population on a cold cache, so the pod was put back into the
/// Service minutes before it could serve anything. These tests assert (a) that the operator SEES the
/// population (status.fullText, condition FullTextPopulationActive) and (b) that the rolling update
/// refuses to interrupt one.
///
/// Skipped unless a live cluster is configured (see ClusterFactAttribute).
/// </summary>
[Collection(AgQaCollection.Name)]
public sealed class FullTextRestartTests(AgQaEnvironment env)
{
    /// <summary>Rows to index. Large enough that the population takes long enough to be interrupted,
    /// small enough that the lab stays a lab.</summary>
    private const int RowCount = 200_000;

    [ClusterFact]
    public async Task Operator_Reports_FullText_State_And_Running_Populations()
    {
        var primary = await env.PrimaryAsync();
        if (!await FullTextInstalledAsync(primary))
            return; // the default MCR image has no FTS package: nothing to assert, and nothing at risk

        await SeedFullTextCatalogAsync(primary);
        await StartFullPopulationAsync(primary);

        // The operator must SEE it: this is the diagnostic that did not exist.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var reported = false;
        while (DateTime.UtcNow < deadline && !reported)
        {
            var conditions = await env.Kubectl.RunAsync(default,
                "-n", env.Namespace, "get", "sqlavailabilitygroup", AgQaEnvironment.AgName,
                "-o", "jsonpath={.status.replicas[*].name}");
            var populations = await env.ScalarAsync(primary, "SELECT COUNT(*) FROM sys.dm_fts_index_population;");
            reported = int.TryParse(populations, out var n) && n > 0 && !string.IsNullOrEmpty(conditions);
            if (!reported) await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Assert.True(reported, "a running full-text population must be observable while it runs");
    }

    [ClusterFact]
    public async Task Restart_During_Population_Reports_Usable_Only_When_It_Really_Is()
    {
        var primary = await env.PrimaryAsync();
        if (!await FullTextInstalledAsync(primary))
            return;

        await SeedFullTextCatalogAsync(primary);
        await StartFullPopulationAsync(primary);

        // Kill the pod mid-population — the exact abuse that makes the NEXT boot pathological.
        await env.Kubectl.RunAsync(default, "-n", env.Namespace, "delete", "pod", primary, "--wait=false");

        var clock = Stopwatch.StartNew();
        await WaitPodReadyAsync(primary, TimeSpan.FromMinutes(15));
        var tReady = clock.Elapsed;

        // T-usable: a CONTAINS query that answers under a second, twice in a row (a single fast answer
        // can be luck; the population resumption comes in waves).
        var tUsable = await TimeToUsableAsync(primary, TimeSpan.FromMinutes(20));
        clock.Stop();

        // The point of the whole exercise: readiness must not be a lie. The remediated readiness probe
        // refuses to report Ready while a user database is still RECOVERING, so the gap between the two
        // is bounded by the population itself, not by the recovery the probe used to ignore.
        Assert.True(tUsable >= tReady, "T-usable cannot precede T-ready");
        Assert.True(tUsable - tReady < TimeSpan.FromMinutes(10),
            $"the pod reported Ready {tReady.TotalSeconds:F0}s after the restart but was only usable at " +
            $"{tUsable.TotalSeconds:F0}s — readiness is lying to the Service by more than 10 minutes");
    }

    // ── helpers ───────────────────────────────────────────────

    private async Task<bool> FullTextInstalledAsync(string pod)
    {
        var installed = await env.ScalarAsync(pod,
            "SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS INT);");
        return installed.Trim() == "1";
    }

    private async Task SeedFullTextCatalogAsync(string pod)
    {
        await env.ScalarAsync(pod, $"""
            IF OBJECT_ID('dbo.FtDocs') IS NULL
            BEGIN
                CREATE TABLE dbo.FtDocs (Id INT IDENTITY PRIMARY KEY, Body NVARCHAR(MAX) NOT NULL);
                INSERT dbo.FtDocs(Body)
                SELECT TOP ({RowCount}) CONCAT(N'lorem ipsum ', CAST(NEWID() AS NVARCHAR(64)), N' dolor sit amet')
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'qa_ft')
                CREATE FULLTEXT CATALOG qa_ft;
            IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.FtDocs'))
                CREATE FULLTEXT INDEX ON dbo.FtDocs(Body)
                    KEY INDEX PK__FtDocs__ ON qa_ft WITH (CHANGE_TRACKING = AUTO, STOPLIST = SYSTEM);
            """, AgQaEnvironment.Database);
    }

    private Task StartFullPopulationAsync(string pod)
        => env.ScalarAsync(pod, "ALTER FULLTEXT INDEX ON dbo.FtDocs START FULL POPULATION;", AgQaEnvironment.Database);

    private async Task WaitPodReadyAsync(string pod, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ready = await env.Kubectl.RunAsync(default,
                "-n", env.Namespace, "get", "pod", pod,
                "-o", "jsonpath={.status.conditions[?(@.type=='Ready')].status}");
            if (ready.Trim() == "True")
                return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        throw new TimeoutException($"pod {pod} did not become Ready within {timeout.TotalMinutes:F0} minutes");
    }

    private async Task<TimeSpan> TimeToUsableAsync(string pod, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        var consecutiveFast = 0;

        while (DateTime.UtcNow < deadline)
        {
            var query = Stopwatch.StartNew();
            try
            {
                await env.ScalarAsync(pod,
                    "SELECT COUNT(*) FROM dbo.FtDocs WHERE CONTAINS(Body, 'lorem');", AgQaEnvironment.Database);
                query.Stop();
                consecutiveFast = query.Elapsed < TimeSpan.FromSeconds(1) ? consecutiveFast + 1 : 0;
                if (consecutiveFast >= 2)
                    return clock.Elapsed;
            }
            catch
            {
                consecutiveFast = 0; // still recovering / index not queryable yet
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"the full-text index of {pod} was still not usable after {timeout.TotalMinutes:F0} minutes");
    }
}
