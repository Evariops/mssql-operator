namespace MsSqlOperator.Entities.Common;

/// <summary>The values <c>status.phase</c> takes across the kinds (one spelling, checked by
/// the compiler, instead of string literals scattered over the state machines).</summary>
public static class Phases
{
    public const string Pending = "Pending";
    public const string Provisioning = "Provisioning";
    public const string Initializing = "Initializing";
    public const string Running = "Running";
    public const string Ready = "Ready";
    public const string Degraded = "Degraded";
    public const string FailingOver = "FailingOver";
    public const string Failed = "Failed";
    public const string Removing = "Removing";
    public const string Conflict = "Conflict";
    public const string Revoked = "Revoked";

    // SqlAvailabilityGroupDatabase
    public const string WaitingForAg = "WaitingForAg";
    public const string Adding = "Adding";
    public const string Seeding = "Seeding";
    public const string Synchronizing = "Synchronizing";
    public const string Synchronized = "Synchronized";
    public const string NotSynchronizing = "NotSynchronizing";
    public const string Suspended = "Suspended";
}

/// <summary>The phases of an availability group's rolling update (<c>status.rollingUpdate.phase</c>).</summary>
public static class RollingUpdatePhases
{
    public const string Idle = "Idle";
    public const string Planning = "Planning";
    public const string UpdatingSecondary = "UpdatingSecondary";
    public const string FailingOverForUpdate = "FailingOverForUpdate";
    public const string UpdatingPrimary = "UpdatingPrimary";
    public const string Failed = "Failed";
}
