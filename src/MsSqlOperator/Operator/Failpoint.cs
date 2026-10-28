using System.Collections.Concurrent;

namespace MsSqlOperator.Operator;

/// <summary>Thrown by a failpoint armed with <c>throw</c>.</summary>
public sealed class FailpointException(string name) : Exception($"failpoint {name} hit");

/// <summary>
/// Places in the code where a test makes the operator fail on purpose — the moments a
/// crash, a pause or a refused write must leave the system safe. Inert unless armed:
/// <c>MSSQLOP_FAILPOINTS="after-intent=throw;status-409=throw;api-latency=delay:2000;after-promotion=exit"</c>
/// (the chaos bench), or <see cref="Arm"/> (unit tests). Actions: <c>throw</c>, <c>delay:&lt;ms&gt;</c>,
/// <c>exit</c> (the process dies there, as a crash would), <c>count:&lt;n&gt;:&lt;action&gt;</c> (only the n-th hit).
/// </summary>
public static class Failpoint
{
    public const string EnvironmentVariable = "MSSQLOP_FAILPOINTS";

    /// <summary>The failpoints the code defines.</summary>
    public static class Names
    {
        public const string AfterIntent = "after-intent";
        public const string AfterPartialIsolation = "after-partial-isolation";
        public const string AfterPromotion = "after-promotion";
        public const string BeforeIntentClose = "before-intent-close";
        public const string Status409 = "status-409";
        public const string ApiLatency = "api-latency";
        public const string WatchCut = "watch-cut";
        public const string WatchSilent = "watch-silent";
        public const string StateReadSlow = "state-read-slow";
        public const string StateReadFail = "state-read-fail";
        public const string WatchdogStopped = "watchdog-stopped";
    }

    private static readonly ConcurrentDictionary<string, string> Armed = new(Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)));
    private static readonly ConcurrentDictionary<string, int> Hits = new();

    public static void Arm(string name, string action) => Armed[name] = action;

    public static void Disarm(string name) => Armed.TryRemove(name, out _);

    public static bool IsArmed(string name) => Armed.ContainsKey(name);

    /// <summary>Does nothing unless <paramref name="name"/> is armed.</summary>
    public static async ValueTask HitAsync(string name, CancellationToken ct = default)
    {
        if (Armed.IsEmpty || !Armed.TryGetValue(name, out var action))
            return;
        var hit = Hits.AddOrUpdate(name, 1, (_, n) => n + 1);
        if (action.StartsWith("count:", StringComparison.Ordinal))
        {
            var parts = action.Split(':', 3);
            if (hit != int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
                return;
            action = parts[2];
        }
        if (action == "throw")
            throw new FailpointException(name);
        if (action == "exit")
            Environment.Exit(97);
        if (action.StartsWith("delay:", StringComparison.Ordinal))
            await Task.Delay(int.Parse(action[6..], System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    internal static IEnumerable<KeyValuePair<string, string>> Parse(string? spec)
        => (spec ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => KeyValuePair.Create(p[0], p[1]));
}
