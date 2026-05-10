namespace MsSqlOperator.Entities.Common;

public static class TtlParser
{
    public static (bool Valid, TimeSpan Duration, string? Error) Parse(string ttl)
    {
        if (string.IsNullOrWhiteSpace(ttl))
            return (false, TimeSpan.Zero, "TTL must not be empty");

        var span = ttl.AsSpan().Trim();
        if (span.Length < 2)
            return (false, TimeSpan.Zero, $"Invalid TTL format: '{ttl}'");

        var suffix = span[^1];
        var numberPart = span[..^1];

        if (!int.TryParse(numberPart, out var value) || value <= 0)
            return (false, TimeSpan.Zero, $"TTL must be a positive integer followed by m, h, or d: '{ttl}'");

        var duration = suffix switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero
        };

        if (duration == TimeSpan.Zero)
            return (false, TimeSpan.Zero, $"Invalid TTL suffix '{suffix}'. Use m (minutes), h (hours), or d (days)");

        return (true, duration, null);
    }
}
