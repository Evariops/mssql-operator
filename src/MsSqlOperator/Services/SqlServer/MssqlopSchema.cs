using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// The <c>mssqlop</c> schema in <c>msdb</c> of every instance and replica (msdb is
/// not replicated): the epoch stamps and the state of the watchdog job; since version 2, what
/// the operator owns there — logins and their server grants, the original values of the settings it
/// changed, the trace flags it turned on — with the custom resource that owns each. Created and
/// migrated by the operator, idempotently and by version.
/// </summary>
public static class MssqlopSchema
{
    public const int Version = 2;

    public static async Task EnsureAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM msdb.sys.schemas WHERE name = N'mssqlop')
                EXEC msdb.sys.sp_executesql N'CREATE SCHEMA mssqlop AUTHORIZATION dbo';
            IF OBJECT_ID(N'msdb.mssqlop.schema_version') IS NULL
                CREATE TABLE msdb.mssqlop.schema_version (version int NOT NULL);
            IF OBJECT_ID(N'msdb.mssqlop.epoch') IS NULL
                CREATE TABLE msdb.mssqlop.epoch (
                    scope sysname NOT NULL CONSTRAINT pk_mssqlop_epoch PRIMARY KEY,
                    epoch bigint NOT NULL,
                    stamped_at datetime2 NOT NULL,
                    stamped_by nvarchar(256) NOT NULL);
            IF OBJECT_ID(N'msdb.mssqlop.watchdog') IS NULL
                CREATE TABLE msdb.mssqlop.watchdog (
                    ag sysname NOT NULL CONSTRAINT pk_mssqlop_watchdog PRIMARY KEY,
                    role nvarchar(60) NULL,
                    last_run datetime2 NULL,
                    synced_secondaries int NULL,
                    below_quorum_since datetime2 NULL,
                    primary_disconnected_since datetime2 NULL,
                    last_decision nvarchar(400) NULL);
            IF OBJECT_ID(N'msdb.mssqlop.owned_principal') IS NULL
                CREATE TABLE msdb.mssqlop.owned_principal (
                    principal sysname NOT NULL CONSTRAINT pk_mssqlop_owned_principal PRIMARY KEY,
                    owner_kind nvarchar(64) NOT NULL,
                    owner_namespace nvarchar(253) NOT NULL,
                    owner_name nvarchar(253) NOT NULL,
                    owner_uid nvarchar(64) NOT NULL,
                    server_grants nvarchar(4000) NULL,
                    recorded_at datetime2 NOT NULL);
            IF OBJECT_ID(N'msdb.mssqlop.owned_setting') IS NULL
                CREATE TABLE msdb.mssqlop.owned_setting (
                    setting nvarchar(128) NOT NULL CONSTRAINT pk_mssqlop_owned_setting PRIMARY KEY,
                    original_value bigint NOT NULL,
                    owner_kind nvarchar(64) NOT NULL,
                    owner_namespace nvarchar(253) NOT NULL,
                    owner_name nvarchar(253) NOT NULL,
                    owner_uid nvarchar(64) NOT NULL,
                    recorded_at datetime2 NOT NULL);
            IF OBJECT_ID(N'msdb.mssqlop.owned_traceflag') IS NULL
                CREATE TABLE msdb.mssqlop.owned_traceflag (
                    traceflag int NOT NULL CONSTRAINT pk_mssqlop_owned_traceflag PRIMARY KEY,
                    owner_kind nvarchar(64) NOT NULL,
                    owner_namespace nvarchar(253) NOT NULL,
                    owner_name nvarchar(253) NOT NULL,
                    owner_uid nvarchar(64) NOT NULL,
                    recorded_at datetime2 NOT NULL);
            IF NOT EXISTS (SELECT 1 FROM msdb.mssqlop.schema_version)
                INSERT INTO msdb.mssqlop.schema_version (version) VALUES (@version);
            ELSE
                UPDATE msdb.mssqlop.schema_version SET version = @version WHERE version < @version;
            """;
        cmd.Parameters.AddWithValue("@version", Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>The epoch stamp of an AG is above the operator's: a newer leader acted.</summary>
public sealed class StaleEpochException(string scope, long epoch, long stamped)
    : InvalidOperationException($"epoch {epoch} is stale for {scope}: a newer leader stamped {stamped}")
{
    public const int SqlErrorNumber = 51002;
    public long Stamped { get; } = stamped;
}

/// <summary>
/// A topology command runs under the applock of its AG and the epoch of its author. The
/// lock is held by the session (<c>@LockOwner = 'Session'</c>) from the stamp to the end of the
/// command, so that two commands on the same replica are serialized — and the watchdog job takes the
/// same lock before it steps a primary down. In Enforce, a stamp above the author's epoch refuses the
/// command (51002): a leader paused and resumed cannot undo what a newer one did. In Shadow the stamp
/// is written (never lowered) and a stale epoch only reported.
/// </summary>
public interface IEpochStamper
{
    /// <summary>Runs <paramref name="command"/> under the applock and the epoch stamp of
    /// <paramref name="scope"/>; returns the stamp found before (0 when none).</summary>
    Task<long> RunAsync(SqlConnection connection, string scope, long epoch, string leader, bool enforce,
        Func<CancellationToken, Task> command, CancellationToken ct);
}

public sealed class SqlEpochStamper : IEpochStamper
{
    public const int ApplockErrorNumber = 51001;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _schemaReady = new();

    public async Task<long> RunAsync(SqlConnection connection, string scope, long epoch, string leader, bool enforce,
        Func<CancellationToken, Task> command, CancellationToken ct)
    {
        if (_schemaReady.TryAdd(connection.DataSource, true))
        {
            try { await MssqlopSchema.EnsureAsync(connection, ct); }
            catch { _schemaReady.TryRemove(connection.DataSource, out _); throw; }
        }

        long stamped;
        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = $"""
                SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
                DECLARE @rc int;
                EXEC @rc = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 5000;
                IF @rc < 0 THROW {ApplockErrorNumber}, N'mssqlop: applock not acquired', 1;
                DECLARE @stamped bigint = ISNULL((SELECT epoch FROM msdb.mssqlop.epoch WHERE scope = @scope), 0);
                IF @stamped > @epoch AND @enforce = 1
                BEGIN
                    EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
                    THROW {StaleEpochException.SqlErrorNumber}, N'mssqlop: stale epoch', 1;
                END
                IF @stamped = 0 AND NOT EXISTS (SELECT 1 FROM msdb.mssqlop.epoch WHERE scope = @scope)
                    INSERT INTO msdb.mssqlop.epoch (scope, epoch, stamped_at, stamped_by) VALUES (@scope, @epoch, SYSUTCDATETIME(), @leader);
                ELSE IF @epoch >= @stamped
                    UPDATE msdb.mssqlop.epoch SET epoch = @epoch, stamped_at = SYSUTCDATETIME(), stamped_by = @leader WHERE scope = @scope;
                SELECT @stamped;
                """;
            acquire.Parameters.AddWithValue("@resource", $"mssqlop:{scope}");
            acquire.Parameters.AddWithValue("@scope", scope);
            acquire.Parameters.AddWithValue("@epoch", epoch);
            acquire.Parameters.AddWithValue("@leader", leader);
            acquire.Parameters.AddWithValue("@enforce", enforce);
            try
            {
                stamped = Convert.ToInt64(await acquire.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (SqlException ex) when (ex.Number == StaleEpochException.SqlErrorNumber)
            {
                throw new StaleEpochException(scope, epoch, await ReadStampAsync(connection, scope, ct));
            }
        }

        try
        {
            await command(ct);
            return stamped;
        }
        finally
        {
            await using var release = connection.CreateCommand();
            release.CommandText = "IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
            release.Parameters.AddWithValue("@resource", $"mssqlop:{scope}");
            try { await release.ExecuteNonQueryAsync(CancellationToken.None); }
            catch (SqlException) { /* the session is gone: so is its lock */ }
        }
    }

    private static async Task<long> ReadStampAsync(SqlConnection connection, string scope, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ISNULL((SELECT epoch FROM msdb.mssqlop.epoch WHERE scope = @scope), 0)";
        cmd.Parameters.AddWithValue("@scope", scope);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
