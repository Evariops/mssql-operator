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
    /// default — Quorum for a new Enterprise AG with 2 synchronous secondaries, Legacy for any other
    /// and for an AG that existed before the profiles.</summary>
    public static string Effective(SqlAvailabilityGroup ag, SqlControlRecord? record)
    {
        if (ag.Spec.Replicas.Durability is { Length: > 0 } explicitProfile)
            return explicitProfile;
        if (record?.Spec.Durability?.Profile is { Length: > 0 } fixedProfile)
            return fixedProfile;
        var isNew = ag.Status?.Phase is null or Phases.Pending or Phases.Initializing;
        return isNew && ag.Spec.Edition == "Enterprise" && SynchronousSecondaries(ag.Spec) >= 2 ? Quorum : Legacy;
    }

    /// <summary>The synchronous secondaries of the topology: every replica but the primary on Standard,
    /// synchronousCount less the primary elsewhere. Quorum needs one to harden each commit (and is not
    /// available on Standard). With a single one, writes block whenever it is down, and after a
    /// failover until the former primary rejoins: the AG must choose that explicitly, so the default
    /// takes Quorum only from 2.</summary>
    public static int SynchronousSecondaries(SqlAvailabilityGroupSpec spec)
        => spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase)
            ? spec.Replicas.Count - 1
            : Math.Min(spec.Replicas.SynchronousCount, spec.Replicas.Count) - 1;

    /// <summary>Whether the engine accepts REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT for this AG. A
    /// basic availability group (Standard) refuses it ("not supported by this edition") and runs at 0:
    /// the primary commits even when its secondary is gone.</summary>
    public static bool SupportsRequiredSync(SqlAvailabilityGroupSpec spec)
        => !spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);

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
