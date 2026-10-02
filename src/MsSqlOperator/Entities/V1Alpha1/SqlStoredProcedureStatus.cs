using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// Slim, etcd-light status: convergence signal + O(1) roll-up scalars only. Per-procedure /
/// per-database deployment detail is no longer stored here — it is served live by the aggregated
/// APIService (<c>storedproceduredeployments</c>), emitted as Events, and counted via metrics.
/// </summary>
public class SqlStoredProcedureStatus
{
    public string Phase { get; set; } = Phases.Pending;
    public long ObservedGeneration { get; set; }
    public string? Source { get; set; }
    public string? SourceRef { get; set; }

    /// <summary>Number of targets (instances/AGs) matched by the selector.</summary>
    public int Targets { get; set; }

    /// <summary>Targets that could not be reconciled this pass.</summary>
    public int FailedTargets { get; set; }

    /// <summary>Procedures defined in the spec (inline + fetched).</summary>
    public int DesiredProcedures { get; set; }

    /// <summary>Procedure deployments actually applied across all targets and databases.</summary>
    public int DeployedProcedures { get; set; }

    /// <summary>The source revision (GitHub tag/sha or inline) last deployed.</summary>
    public string? LastDeployedRevision { get; set; }

    /// <summary>
    /// Commit SHA the GitHub source is currently pinned at: tags and
    /// followLatestRelease are resolved to a commit at reconcile time for traceability;
    /// an event is emitted whenever this changes.
    /// </summary>
    public string? ResolvedCommitSha { get; set; }

    /// <summary>When a mutable GitHub ref (tag, latest release) was last resolved: it is re-resolved
    /// once per <c>sync.intervalMinutes</c>.</summary>
    public DateTime? LastSourceSyncTime { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
