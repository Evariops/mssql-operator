using System.Collections.Concurrent;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Control;

/// <summary>The durability profile of an AG and what it implies.</summary>
public static class DurabilityProfile
{
    public const string Quorum = "Quorum";
    public const string Legacy = "Legacy";
    public const string AtRiskCondition = "DurabilityAtRisk";

    /// <summary>The profile fixed for each AG, as this process last read it from the control record.</summary>
    private static readonly ConcurrentDictionary<string, string> Fixed = new();

    /// <summary>The profile in force: the spec's, else the one fixed in the control record, else the
    /// default — Quorum for a new Enterprise AG that allows it, Legacy for any other and for an AG
    /// that existed before the profiles.</summary>
    public static string Effective(SqlAvailabilityGroup ag, SqlControlRecord? record)
    {
        if (ag.Spec.Replicas.Durability is { Length: > 0 } explicitProfile)
            return explicitProfile;
        if (record?.Spec.Durability?.Profile is { Length: > 0 } fixedProfile)
            return fixedProfile;
        var isNew = ag.Status?.Phase is null or Phases.Pending or Phases.Initializing;
        return isNew && ag.Spec.Edition == "Enterprise" && AllowsQuorum(ag.Spec.Replicas) ? Quorum : Legacy;
    }

    /// <summary>Quorum needs at least 2 synchronous secondaries: 3 replicas, 3 of them synchronous.
    /// The webhook refuses an explicit Quorum below that, and the default follows the same rule: a
    /// default Quorum with 1 synchronous secondary would block writes without giving its guarantee.</summary>
    public static bool AllowsQuorum(AgReplicaTopologySpec replicas)
        => replicas is { Count: >= 3, SynchronousCount: >= 3 };

    /// <summary>Remembers the profile fixed for the AG (the AG controller, from its control record).</summary>
    public static void Remember(SqlAvailabilityGroup ag, string profile) => Fixed[Key(ag)] = profile;

    /// <summary>Whether this process has read the profile of the AG since it started.</summary>
    public static bool IsKnown(SqlAvailabilityGroup ag) => Fixed.ContainsKey(Key(ag));

    /// <summary>The profile the durability floors apply: the spec's, else the one remembered for the
    /// AG, else the default. An AG that does not set the field takes its profile from the control
    /// record; before it was read, an AG already running would fall back to Legacy.</summary>
    public static string InForce(SqlAvailabilityGroup ag)
        => ag.Spec.Replicas.Durability ?? Fixed.GetValueOrDefault(Key(ag)) ?? Effective(ag, null);

    /// <summary>The floor of REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT: at least 1 in Quorum.</summary>
    public static int Floor(SqlAvailabilityGroup ag, string profile)
    {
        var floor = Math.Max(0, ag.Spec.Replicas.RequiredSyncFloor);
        return profile == Quorum ? Math.Max(1, floor) : floor;
    }

    private static string Key(SqlAvailabilityGroup ag) => ag.Metadata.Uid ?? ag.Metadata.Name;
}
