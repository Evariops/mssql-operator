using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Whether the topology of an availability group is changing — a failover (intent recorded
/// or phase FailingOver), the fencing of the previous primary, or the primary's turn in a rolling
/// update. A RESUME, a re-seed or the removal of a database issued then acts on a primary that is
/// about to stop being one, or on a replica the failover is still sorting out.
/// </summary>
public static class AgTopology
{
    public const string Condition = "TopologyChangeInProgress";

    /// <summary>What is changing, or null when the topology is stable.</summary>
    public static string? ChangeInProgress(SqlAvailabilityGroup ag)
    {
        var status = ag.Status;
        if (status is null)
            return null;
        if (status.Phase == Phases.FailingOver)
            return $"availability group {ag.Metadata.Name} is failing over";
        if (status.Fencing is not null)
            return $"the previous primary of {ag.Metadata.Name} is being fenced";
        if (status.FailoverIntent is { } intent)
            return $"a failover of {ag.Metadata.Name} to {intent.To} is in progress";
        if (status.RollingUpdate?.Phase is RollingUpdatePhases.UpdatingPrimary or RollingUpdatePhases.FailingOverForUpdate)
            return $"the rolling update of {ag.Metadata.Name} is moving the primary";
        // During a split-brain, or while a replica answering PRIMARY is not trusted as the primary,
        // status.primaryReplica may name the wrong side: a RESUME or a re-seed driven from it would
        // act on the wrong timeline.
        if (status.Conditions?.FirstOrDefault(c => c.Type == SplitBrainSafeCondition) is { Status: "False" })
            return $"availability group {ag.Metadata.Name} has no trusted primary (split-brain or a resurrected previous primary)";
        return null;
    }

    public const string SplitBrainSafeCondition = "SplitBrainSafe";

    /// <summary>Whether the topology wants replica <paramref name="ordinal"/> in synchronous commit:
    /// always on Standard, else by its override, else among the first synchronousCount.</summary>
    public static bool DesignatedSynchronous(SqlAvailabilityGroup ag, int ordinal)
    {
        if (ag.Spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase))
            return true;
        var ov = ag.Spec.Replicas.Overrides?.FirstOrDefault(o => o.Ordinal == ordinal);
        return ov?.AvailabilityMode switch
        {
            "AsynchronousCommit" => false,
            "SynchronousCommit" => true,
            _ => ordinal < ag.Spec.Replicas.SynchronousCount,
        };
    }
}
