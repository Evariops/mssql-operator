namespace MsSqlOperator.Entities.Common;

/// <summary>A daily window, in UTC, in which a heavy operation may start.</summary>
public class MaintenanceWindowSpec
{
    /// <summary>Opening of the window, <c>HH:mm</c> in UTC.</summary>
    public string Start { get; set; } = "00:00";

    /// <summary>Length of the window in minutes (1–1440). Defaults to 240.</summary>
    public int DurationMinutes { get; set; } = 240;
}
