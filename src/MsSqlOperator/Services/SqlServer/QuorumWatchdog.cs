using System.Globalization;
using Microsoft.Data.SqlClient;
using MsSqlOperator.Core.Timing;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>What the watchdog job of a replica last wrote (msdb.mssqlop.watchdog).</summary>
/// <param name="Role">PRIMARY or SECONDARY at its last run.</param>
/// <param name="AgeSeconds">Seconds since its last run, measured by the replica's own clock.</param>
/// <param name="SyncedSecondaries">For a primary: the synchronized secondaries it saw — its quorum.</param>
/// <param name="BelowQuorumSeconds">For a primary: how long it has been below its quorum.</param>
/// <param name="PrimaryDisconnectedSeconds">For a secondary: how long it has lost the primary, by its own clock.</param>
/// <param name="LastDecision">What it decided at its last run.</param>
public sealed record WatchdogState(string? Role, int? AgeSeconds, int? SyncedSecondaries, int? BelowQuorumSeconds,
    int? PrimaryDisconnectedSeconds, string? LastDecision);

/// <summary>
/// The quorum watchdog — a SQL Agent job on every replica, every J seconds, owned by the
/// operator and replaced atomically. On the primary, fewer than k_eff synchronized secondaries
/// for T_step takes the AG OFFLINE, under the AG's applock: a primary cut from its quorum
/// stops acknowledging on its own, whether the operator is there or not (I11), and before an election
/// can promote another (I10, T_vote &gt;= T_step + J + S). On a secondary it records when the primary
/// was lost — the T_vote the elections read. It reads nothing of the operator. In Shadow it writes
/// the decision it would have taken, without acting.
/// </summary>
public static class QuorumWatchdog
{
    public const int Version = 1;

    public static string JobName(string ag) => $"mssqlop-watchdog-{ag}";

    public static string Marker(TimingBudget budget, bool enforce)
        => string.Create(CultureInfo.InvariantCulture, $"mssqlop-watchdog v{Version} enforce={enforce} tstep={(int)budget.StepDown.TotalSeconds}");

    public static JobDefinition Definition(string ag, TimingBudget budget, bool enforce, bool enabled = true)
        => new(JobName(ag), Marker(budget, enforce), enabled, null, "sa",
            [new JobStepDefinition("quorum", 1, "TSQL", "master", Script(ag, budget, enforce), "QuitWithSuccess", "QuitWithFailure", 0, 0)],
            [new JobScheduleDefinition($"{JobName(ag)}-every-{(int)budget.WatchdogPeriod.TotalSeconds}s", true, 4, 1, 0,
                SubdayType: 2, SubdayInterval: (int)budget.WatchdogPeriod.TotalSeconds, ActiveStartTime: 0, ActiveStartDate: null)],
            null);

    /// <summary>The job step. A job step takes no parameters: the AG name is quoted as a literal.</summary>
    public static string Script(string ag, TimingBudget budget, bool enforce)
    {
        var name = SqlQuoting.QuoteNStringLiteral(ag);
        var resource = SqlQuoting.QuoteNStringLiteral($"mssqlop:{ag}");
        var tstep = ((int)budget.StepDown.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var enforceBit = enforce ? "1" : "0";
        return $"""
            SET NOCOUNT ON;
            DECLARE @ag sysname = {name}, @tstep int = {tstep}, @enforce bit = {enforceBit}, @now datetime2 = SYSUTCDATETIME();
            DECLARE @group uniqueidentifier = (SELECT group_id FROM sys.availability_groups WHERE name = @ag);
            IF @group IS NULL OR OBJECT_ID(N'msdb.mssqlop.watchdog') IS NULL RETURN;
            DECLARE @role nvarchar(60), @connected nvarchar(60);
            SELECT @role = role_desc, @connected = connected_state_desc
              FROM sys.dm_hadr_availability_replica_states WHERE group_id = @group AND is_local = 1;
            DECLARE @k int = (SELECT required_synchronized_secondaries_to_commit FROM sys.availability_groups WHERE group_id = @group);
            DECLARE @keff int = CASE WHEN ISNULL(@k, 0) < 1 THEN 1 ELSE @k END, @synced int = NULL;
            IF @role = N'PRIMARY'
                SELECT @synced = COUNT(*) FROM sys.dm_hadr_availability_replica_states rs
                  JOIN sys.availability_replicas ar ON ar.replica_id = rs.replica_id
                 WHERE rs.group_id = @group AND rs.is_local = 0 AND rs.connected_state_desc = N'CONNECTED'
                   AND rs.synchronization_health_desc = N'HEALTHY' AND ar.availability_mode_desc = N'SYNCHRONOUS_COMMIT';
            DECLARE @below datetime2 = (SELECT below_quorum_since FROM msdb.mssqlop.watchdog WHERE ag = @ag);
            DECLARE @lost datetime2 = (SELECT primary_disconnected_since FROM msdb.mssqlop.watchdog WHERE ag = @ag);
            SET @below = CASE WHEN @role = N'PRIMARY' AND @synced < @keff THEN ISNULL(@below, @now) ELSE NULL END;
            -- On a secondary, the local row says whether it is connected to the primary.
            SET @lost = CASE WHEN @role = N'SECONDARY' AND @connected <> N'CONNECTED' THEN ISNULL(@lost, @now) ELSE NULL END;
            DECLARE @decision nvarchar(400) = N'ok';
            IF @below IS NOT NULL AND DATEDIFF(second, @below, @now) >= @tstep
            BEGIN
                IF @enforce = 1
                BEGIN
                    DECLARE @rc int;
                    EXEC @rc = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 5000;
                    IF @rc >= 0
                    BEGIN
                        DECLARE @sql nvarchar(max) = N'ALTER AVAILABILITY GROUP ' + QUOTENAME(@ag) + N' OFFLINE';
                        EXEC sys.sp_executesql @sql;
                        EXEC sp_releaseapplock @Resource = {resource}, @LockOwner = 'Session';
                        SET @decision = N'offline: no quorum for ' + CAST(DATEDIFF(second, @below, @now) AS nvarchar(20)) + N' s';
                    END
                    ELSE SET @decision = N'offline deferred: applock busy';
                END
                ELSE SET @decision = N'shadow: would take the AG offline (no quorum for ' + CAST(DATEDIFF(second, @below, @now) AS nvarchar(20)) + N' s)';
            END
            MERGE msdb.mssqlop.watchdog AS t
            USING (SELECT @ag AS ag) AS s ON t.ag = s.ag
            WHEN MATCHED THEN UPDATE SET role = @role, last_run = @now, synced_secondaries = @synced,
                below_quorum_since = @below, primary_disconnected_since = @lost, last_decision = @decision
            WHEN NOT MATCHED THEN INSERT (ag, role, last_run, synced_secondaries, below_quorum_since, primary_disconnected_since, last_decision)
                VALUES (@ag, @role, @now, @synced, @below, @lost, @decision);
            """;
    }

    /// <summary>The state the watchdog of the replica behind <paramref name="connection"/>
    /// last wrote, with ages computed by that replica's own clock; null when it never ran.</summary>
    public static async Task<WatchdogState?> ReadAsync(SqlConnection connection, string ag, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'msdb.mssqlop.watchdog') IS NULL SELECT TOP 0 NULL AS role;
            ELSE
            SELECT role, DATEDIFF(second, last_run, SYSUTCDATETIME()), synced_secondaries,
                   DATEDIFF(second, below_quorum_since, SYSUTCDATETIME()), DATEDIFF(second, primary_disconnected_since, SYSUTCDATETIME()), last_decision
              FROM msdb.mssqlop.watchdog WHERE ag = @ag
            """;
        cmd.Parameters.AddWithValue("@ag", ag);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.FieldCount < 6)
            return null;
        int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
        return new WatchdogState(reader.IsDBNull(0) ? null : reader.GetString(0), Int(1), Int(2), Int(3), Int(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    /// <summary>Alive: it ran within the last three periods.</summary>
    public static bool Alive(WatchdogState? state, TimingBudget budget)
        => state?.AgeSeconds is { } age && age <= 3 * budget.WatchdogPeriod.TotalSeconds;
}
