using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

/// <summary>The log chain an availability-group database needs, on a real engine.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlAgManagerTests(SqlServerFixture fixture)
{
    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<object?> LastLogBackupLsnAsync(SqlConnection connection, string db) => ScalarAsync(connection,
        $"SELECT drs.last_log_backup_lsn FROM sys.database_recovery_status drs WHERE drs.database_id = DB_ID('{db}')");

    [Fact]
    public async Task NeverBackedUpDatabase_ChainIsStarted_AndTheNextPassTakesNoBackup()
    {
        // A COPY_ONLY full left last_log_backup_lsn NULL: every pass took another full backup, and
        // ADD DATABASE failed with 1475 after the minimally logged inserts of a fresh database.
        await using var connection = await fixture.CreateConnectionAsync();
        var db = $"Chain_{Guid.NewGuid():N}"[..20];
        await ExecAsync(connection, $"CREATE DATABASE [{db}]");
        await ExecAsync(connection, $"ALTER DATABASE [{db}] SET RECOVERY FULL");
        await ExecAsync(connection, $"""
            CREATE TABLE [{db}].dbo.T (Id INT IDENTITY PRIMARY KEY, Payload CHAR(200) NOT NULL);
            INSERT [{db}].dbo.T WITH (TABLOCK) (Payload)
            SELECT TOP (20000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        Assert.Equal(DBNull.Value, await LastLogBackupLsnAsync(connection, db));
        var manager = new SqlAgManager();

        var first = await manager.PrepareDatabaseForAgAsync(connection, db, $"/var/opt/mssql/data/{db}_ag_init.bak");
        var second = await manager.PrepareDatabaseForAgAsync(connection, db, $"/var/opt/mssql/data/{db}_ag_init.bak");

        Assert.True(first);
        Assert.NotEqual(DBNull.Value, await LastLogBackupLsnAsync(connection, db));
        Assert.False(second);
    }

    [Fact]
    public async Task SeedingState_QueryRunsOnTheRealEngine()
    {
        // It ordered by a column the DMV does not have and failed on every pass. There is
        // no availability group here: the query must simply run and find nothing.
        await using var connection = await fixture.CreateConnectionAsync();

        var state = await new SqlAgManager().GetSeedingStateAsync(connection, "no-such-ag", "no-such-db");

        Assert.Null(state);
    }

    [Fact]
    public async Task ChainBrokenBySimpleRecovery_DifferentialBaseIsKept()
    {
        // A backup plan's full, then SIMPLE→FULL: the chain is broken but the differential base of
        // the plan exists. A full backup would have reset it; a differential one keeps it.
        await using var connection = await fixture.CreateConnectionAsync();
        var db = $"Base_{Guid.NewGuid():N}"[..20];
        await ExecAsync(connection, $"CREATE DATABASE [{db}]");
        await ExecAsync(connection, $"ALTER DATABASE [{db}] SET RECOVERY SIMPLE");
        await ExecAsync(connection, $"BACKUP DATABASE [{db}] TO DISK = '/var/opt/mssql/data/{db}_plan.bak' WITH FORMAT, INIT");
        await ExecAsync(connection, $"ALTER DATABASE [{db}] SET RECOVERY FULL");
        var planBase = await ScalarAsync(connection,
            $"SELECT differential_base_guid FROM sys.master_files WHERE database_id = DB_ID('{db}') AND type = 0");

        var taken = await new SqlAgManager().PrepareDatabaseForAgAsync(connection, db, $"/var/opt/mssql/data/{db}_ag_init.bak");

        Assert.True(taken);
        Assert.NotEqual(DBNull.Value, await LastLogBackupLsnAsync(connection, db));
        Assert.Equal(planBase, await ScalarAsync(connection,
            $"SELECT differential_base_guid FROM sys.master_files WHERE database_id = DB_ID('{db}') AND type = 0"));
    }
}
