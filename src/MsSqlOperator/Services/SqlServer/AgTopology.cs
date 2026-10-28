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
        return null;
    }
}
