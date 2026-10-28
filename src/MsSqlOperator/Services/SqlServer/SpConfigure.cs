using System.Text;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>Another sp_configure value is armed and not committed — by a DBA, most likely.
/// The operator's RECONFIGURE would have committed it for them; nothing was changed.</summary>
public sealed class ReconfigureBlockedException(IReadOnlyList<string> pending)
    : InvalidOperationException(
        $"sp_configure values armed by someone else are waiting for a RECONFIGURE: {string.Join(", ", pending)}. The operator " +
        "commits nothing until they are committed (RECONFIGURE) or reverted (sp_configure back to value_in_use).")
{
    public const int SqlErrorNumber = 51020;

    public IReadOnlyList<string> Pending { get; } = pending;
}

/// <summary>
/// Sets sp_configure values and commits them, in one batch. RECONFIGURE commits every pending
/// value, not only the operator's: one a DBA armed and had not committed yet (to commit it at a quiet
/// time) went live at the operator's next pass. The batch first checks that no other dynamic option
/// is pending — <c>value &lt;&gt; value_in_use</c> — and changes nothing when one is. An option that
/// needs a restart (<c>is_dynamic = 0</c>) keeps a different value_in_use until then whatever
/// RECONFIGURE does, so it does not count. 'show advanced options' is raised only when it is off, and
/// put back. Neither does 'min server memory (MB)' configured below 16 — the
/// engine runs it at 16 whatever RECONFIGURE does (0 / 16 on every fresh server), and the guard
/// refused every sp_configure value of every instance.
/// </summary>
public static class SpConfigure
{
    private const string AdvancedOptions = "show advanced options";

    public static async Task ApplyAsync(SqlConnection connection, IReadOnlyDictionary<string, int> values, CancellationToken ct)
    {
        if (values.Count == 0)
            return;

        var sql = new StringBuilder($"""
            DECLARE @pending nvarchar(max) = (
                SELECT STRING_AGG(CAST(name AS nvarchar(max)), N', ')
                FROM sys.configurations
                WHERE is_dynamic = 1 AND CAST(value AS bigint) <> CAST(value_in_use AS bigint)
                  AND name <> N'show advanced options'
                  AND NOT (name = N'min server memory (MB)' AND CAST(value AS bigint) < 16 AND CAST(value_in_use AS bigint) = 16)
                  AND name NOT IN (SELECT value FROM STRING_SPLIT(@ours, N'|')));
            IF @pending IS NOT NULL
            BEGIN
                DECLARE @message nvarchar(2048) = LEFT(@pending, 2048);
                THROW {ReconfigureBlockedException.SqlErrorNumber}, @message, 1;
            END
            DECLARE @advanced int = (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'show advanced options');
            IF @advanced = 0
            BEGIN
                EXEC sp_configure N'show advanced options', 1;
                RECONFIGURE;
            END

            """);
        await using var cmd = connection.CreateCommand();
        var i = 0;
        foreach (var (name, value) in values)
        {
            sql.AppendLine($"EXEC sp_configure @name{i}, @value{i};");
            cmd.Parameters.AddWithValue($"@name{i}", name);
            cmd.Parameters.AddWithValue($"@value{i}", value);
            i++;
        }
        if (!values.Keys.Contains(AdvancedOptions, StringComparer.OrdinalIgnoreCase))
            sql.AppendLine("IF @advanced = 0 EXEC sp_configure N'show advanced options', 0;");
        sql.AppendLine("RECONFIGURE;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("@ours", string.Join('|', values.Keys));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == ReconfigureBlockedException.SqlErrorNumber)
        {
            throw new ReconfigureBlockedException(ex.Message.Split(", ", StringSplitOptions.RemoveEmptyEntries));
        }
    }
}

/// <summary>The <c>ReconfigureBlocked</c> condition, shared by everything that sets sp_configure values.</summary>
public static class ReconfigureGuard
{
    public const string Condition = "ReconfigureBlocked";

    public static void Report(List<Entities.Common.ConditionStatus> conditions, ReconfigureBlockedException blocked)
        => MsSqlOperator.Operator.Abstractions.ConditionOps.Set(conditions, Condition, "True", Condition, blocked.Message);

    public static void Clear(List<Entities.Common.ConditionStatus>? conditions)
    {
        if (conditions?.FirstOrDefault(c => c.Type == Condition) is { Status: "True" })
            MsSqlOperator.Operator.Abstractions.ConditionOps.Set(conditions, Condition, "False", "NothingPending",
                "No sp_configure value armed by someone else is waiting for a RECONFIGURE.");
    }
}
