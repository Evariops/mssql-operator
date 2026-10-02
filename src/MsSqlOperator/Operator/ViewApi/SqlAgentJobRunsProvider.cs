using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>sqlagentjobruns</c>: live SQL Agent job run state, computed from <c>msdb.dbo.sysjobs</c>,
/// <c>sysjobhistory</c> and <c>sysjobactivity</c> for every target each SqlAgentJob selects. Replaces
/// the removed last-run/next-run detail that used to live in <c>SqlAgentJob.status</c>. One row per
/// (job, target); for AG targets the primary replica (where the job actually runs) is queried.
/// </summary>
public sealed class SqlAgentJobRunsProvider(
    ILogger<SqlAgentJobRunsProvider> logger,
    IKubernetesClient kubernetesClient,
    ITargetResolver targetResolver,
    ISqlConnectionFactory connectionFactory,
    ISqlAgentManager agentManager) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "sqlagentjobruns",
        SingularName = "sqlagentjobrun",
        Kind = "SqlAgentJobRun",
        ShortNames = ["jobrun"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Job", Type = "string" },
        new() { Name = "Target", Type = "string" },
        new() { Name = "Exists", Type = "boolean" },
        new() { Name = "LastRun", Type = "date" },
        new() { Name = "LastStatus", Type = "string" },
        new() { Name = "NextRun", Type = "date" },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var jobs = await kubernetesClient.ListAsync<SqlAgentJob>(@namespace, null, ct);

        foreach (var jobCr in jobs)
        {
            var job = jobCr.Spec.Job;
            if (job is null || string.IsNullOrWhiteSpace(job.Name)) continue;
            var jobNs = jobCr.Metadata.NamespaceProperty;

            List<ResolvedTarget> targets;
            try { targets = await targetResolver.ResolveAsync(jobCr.Spec.Target, jobNs, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "view: resolve targets for {Job}", jobCr.Metadata.Name); continue; }

            foreach (var target in targets)
            {
                try
                {
                    await using var conn = await ViewTargets.OpenAsync(target, connectionFactory, targetResolver, ct);
                    if (conn is null)
                    {
                        logger.LogWarning("view: no reachable primary for {Job}/{Target}", jobCr.Metadata.Name, target.Name);
                        continue;
                    }

                    var exists = await agentManager.GetJobIdAsync(conn, job.Name, ct) is not null;
                    DateTime? lastRun = null, nextRun = null;
                    string? lastStatus = null;
                    if (exists)
                        (lastRun, lastStatus, nextRun) = await agentManager.GetJobStatusAsync(conn, job.Name, ct);

                    items.Add(new Dictionary<string, object?>
                    {
                        ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
                        ["kind"] = "SqlAgentJobRun",
                        ["metadata"] = new Dictionary<string, object?> { ["name"] = ViewItems.Sanitize($"{jobCr.Metadata.Name}.{target.Name}"), ["namespace"] = jobNs },
                        ["resource"] = jobCr.Metadata.Name,
                        ["job"] = job.Name,
                        ["target"] = target.Name,
                        ["exists"] = exists,
                        ["lastRun"] = lastRun,
                        ["lastStatus"] = lastStatus,
                        ["nextRun"] = nextRun,
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "view: agent-job state for {Job}/{Target}", jobCr.Metadata.Name, target.Name);
                }
            }
        }

        return name is null ? items : items.Where(i => ViewItems.Name(i) == name).ToList();
    }

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells =
        [
            ViewItems.Name(item), item.GetValueOrDefault("job"), item.GetValueOrDefault("target"),
            item.GetValueOrDefault("exists"), Iso(item.GetValueOrDefault("lastRun")),
            item.GetValueOrDefault("lastStatus"), Iso(item.GetValueOrDefault("nextRun")),
        ]
    };

    private static string? Iso(object? v) => v is DateTime dt ? dt.ToString("yyyy-MM-dd") : null;
}
