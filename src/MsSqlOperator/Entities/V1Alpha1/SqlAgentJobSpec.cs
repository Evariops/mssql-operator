using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAgentJobSpec
{
    /// <summary>What the operator may do on its own for this resource (Observe, Plan,
    /// Apply), and the destructive actions pre-authorized within its budget.</summary>
    public MsSqlOperator.Entities.Common.AutomationSpec? Automation { get; set; }

    public TargetSelector Target { get; set; } = new();
    public JobDefinitionSpec? Job { get; set; }
}

public class JobDefinitionSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Category { get; set; }

    /// <summary>Job owner login. Defaults to <c>sa</c> — see <see cref="AllowDangerous"/>.</summary>
    public string? Owner { get; set; }
    public List<JobStepSpec>? Steps { get; set; }
    public List<JobScheduleSpec>? Schedules { get; set; }
    public JobNotificationSpec? Notifications { get; set; }

    /// <summary>
    /// Required opt-in for a job that runs with full instance privileges.
    ///
    /// A job owned by <c>sa</c> executes its steps as sysadmin: a T-SQL step can then turn on
    /// <c>xp_cmdshell</c> and call it, which is precisely the capability the sp_configure allowlist
    /// exists to gate — the job path bypassed it entirely. Non-T-SQL steps (CmdExec, PowerShell) run
    /// operating-system commands outright. Both now require this explicit, review-visible flag.
    /// Leave it false and give the job a non-privileged owner instead.
    /// </summary>
    public bool AllowDangerous { get; set; }
}

public class JobStepSpec
{
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; } = 1;
    public string Type { get; set; } = "TSQL";
    public string? Database { get; set; }
    public string Command { get; set; } = string.Empty;
    public string OnSuccess { get; set; } = "GoToNextStep";
    public string OnFailure { get; set; } = "QuitWithFailure";

    /// <summary>Target step when OnSuccess/OnFailure is GoToStep: msdb's
    /// @on_success_step_id/@on_fail_step_id need it, and it was previously inexpressible.</summary>
    public int? OnSuccessStep { get; set; }
    public int? OnFailureStep { get; set; }

    public int RetryAttempts { get; set; }
    public int RetryIntervalMinutes { get; set; } = 5;
}

public class JobScheduleSpec
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string FrequencyType { get; set; } = "Daily";

    /// <summary>Daily: days between runs. Weekly: bitmask of days (1 Sunday … 64 Saturday).
    /// Monthly: day of the month.</summary>
    public int FrequencyInterval { get; set; } = 1;

    /// <summary>Weekly/Monthly: weeks or months between runs (msdb freq_recurrence_factor).</summary>
    public int RecurrenceFactor { get; set; } = 1;

    /// <summary>Repetition within the day: <c>Once</c> (at startTime), <c>Hours</c>, <c>Minutes</c>
    /// or <c>Seconds</c>, every <see cref="SubdayInterval"/> — e.g. a log backup every 15 minutes.</summary>
    public string SubdayType { get; set; } = "Once";

    public int SubdayInterval { get; set; }

    /// <summary>HHmmss.</summary>
    public string? StartTime { get; set; }

    /// <summary>yyyyMMdd.</summary>
    public string? StartDate { get; set; }
}

public class JobNotificationSpec
{
    public NotificationAction? OnFailure { get; set; }
    public NotificationAction? OnCompletion { get; set; }
}

public class NotificationAction
{
    public string? Operator { get; set; }
    public string? NotifyMethod { get; set; }
    public bool WriteToEventLog { get; set; }
}
