using System.Globalization;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>Whether a daily UTC window is open.</summary>
public static class MaintenanceWindows
{
    public static bool TryParseStart(string? start, out TimeSpan opening)
    {
        var ok = TimeSpan.TryParseExact(start, @"hh\:mm", CultureInfo.InvariantCulture, out opening);
        return ok && opening < TimeSpan.FromDays(1);
    }

    /// <summary>Open at <paramref name="now"/>; a window may run past midnight. An unparsable one
    /// (refused at admission) is closed.</summary>
    public static bool IsOpen(MaintenanceWindowSpec window, DateTimeOffset now)
    {
        if (!TryParseStart(window.Start, out var opening))
            return false;
        var duration = TimeSpan.FromMinutes(Math.Clamp(window.DurationMinutes, 1, 1440));
        var sinceOpening = now.UtcDateTime.TimeOfDay - opening;
        if (sinceOpening < TimeSpan.Zero)
            sinceOpening += TimeSpan.FromDays(1);
        return sinceOpening < duration;
    }
}
