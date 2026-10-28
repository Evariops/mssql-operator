namespace MsSqlOperator.Core.Safety;

/// <summary>
/// The behaviour changes of the high-availability chain come in three ordered levels, each
/// including the previous ones. 0: the current behaviour and Phase 0. 1: registry, SQL epoch,
/// actuation gateway. 2: quorum elections, autonomous step-down, routing by name, no fencing.
/// 3: event-driven supervision.
/// </summary>
public static class SafetyLevel
{
    public const int Max = 3;

    /// <summary>Per AG: <c>mssql-operator.evariops.com/safety-level</c>.</summary>
    public const string Annotation = "mssql-operator.evariops.com/safety-level";

    /// <summary>The level an AG runs at: its annotation, capped by the operator's ceiling
    /// (Operator:SafetyLevelMax, Helm operator.safetyLevelMax); 0 when absent or unreadable.</summary>
    public static int Effective(string? annotation, int ceiling)
        => Math.Clamp(int.TryParse(annotation, out var level) ? level : 0, 0, Math.Clamp(ceiling, 0, Max));

    /// <summary>The level computed in Shadow next to the effective one — its decisions are logged and
    /// compared, not applied — or null at the ceiling.</summary>
    public static int? Shadow(int effective, int ceiling) => effective < Math.Clamp(ceiling, 0, Max) ? effective + 1 : null;

    /// <summary>Whether <paramref name="level"/> is applied (Enforce) or only computed (Shadow) for an
    /// AG at <paramref name="effective"/>; null when it does not run at all.</summary>
    public static SafetyMode? Mode(int level, int effective, int ceiling)
        => level <= effective ? SafetyMode.Enforce : Shadow(effective, ceiling) == level ? SafetyMode.Shadow : null;
}

public enum SafetyMode { Shadow, Enforce }

/// <summary>The switches outside the chain, each with Off, Shadow and Enforce.</summary>
public enum SwitchMode { Off, Shadow, Enforce }

/// <summary>A level or a switch, and the version by which its old code path must be gone.</summary>
public sealed record TransitionalSetting(string Name, string RemoveBy);

public static class TransitionalSettings
{
    /// <summary>A build fails once the current version reaches one of these (test).</summary>
    public static IReadOnlyList<TransitionalSetting> All { get; } =
    [
        new("SafetyLevel1", "2.0.0"),
        new("SafetyLevel2", "2.0.0"),
        new("SafetyLevel3", "2.0.0"),
        new("TargetCircuitBreakers", "2.0.0"),
        new("CapabilityLogins", "2.0.0"),
        new("StrictTls", "2.0.0"),
    ];
}
