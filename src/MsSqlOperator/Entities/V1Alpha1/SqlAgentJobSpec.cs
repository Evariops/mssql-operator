using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlAgentJobSpec
{
    public ResourceReference? InstanceRef { get; set; }
    public JobDefinitionSpec? Job { get; set; }
}

public class JobDefinitionSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Category { get; set; }
    public string? Owner { get; set; }
    public List<JobStepSpec>? Steps { get; set; }
    public List<JobScheduleSpec>? Schedules { get; set; }
    public JobNotificationSpec? Notifications { get; set; }
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
    public int RetryAttempts { get; set; }
    public int RetryIntervalMinutes { get; set; } = 5;
}

public class JobScheduleSpec
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string FrequencyType { get; set; } = "Daily";
    public int FrequencyInterval { get; set; } = 1;
    public string? StartTime { get; set; }
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
