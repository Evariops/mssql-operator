using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlInstanceStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }
    public string? Version { get; set; }
    public string? Edition { get; set; }
    public string? Endpoint { get; set; }
    public MemoryStatus? Memory { get; set; }

    /// <summary>Full-Text Search state: what the operator was previously blind to.</summary>
    public FullTextStatus? FullText { get; set; }
    public List<PodStatus>? PodStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class PodStatus
{
    public string Name { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public string? Role { get; set; }
}

/// <summary>
/// Full-Text Search state of an instance. The operator had no idea whether full-text
/// was even installed, let alone whether a population was running — so the failure it
/// caused (pods taking many minutes to become genuinely usable after a restart, because the engine
/// resumes interrupted population crawls at startup) was invisible to every signal the operator
/// published.
/// </summary>
public class FullTextStatus
{
    /// <summary>Whether the Full-Text Search package is installed on this instance.</summary>
    public bool Installed { get; set; }

    /// <summary>sp_fulltext_service 'upgrade_option': Import | Rebuild | Reset. Rebuild/Reset
    /// repopulate every catalogue on the first boot after an engine upgrade.</summary>
    public string? UpgradeOption { get; set; }

    /// <summary>Population crawls currently queued or running.</summary>
    public int ActivePopulations { get; set; }
}
