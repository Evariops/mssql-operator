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
    public async Task LogUsage_IsReadForAnyDatabaseFromMaster()
    {
        // The log usage is read through the performance counter, which answers for any
        // database from master (sys.dm_db_log_space_usage only knows the current one).
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgManager();

        var usage = await manager.GetLogUsageAsync(connection, "msdb");
        var missing = await manager.GetLogUsageAsync(connection, "no-such-db");

        Assert.NotNull(usage);
        Assert.InRange(usage.PercentUsed, 0, 100);
        Assert.False(string.IsNullOrEmpty(usage.ReuseWait));
        Assert.Null(missing);
    }

    [Fact]
    public async Task ReplicaSeeding_QueryRunsOnTheRealEngine()
    {
        // The query must bind and run on a real engine; there is no AG here, so it finds nothing.
        await using var connection = await fixture.CreateConnectionAsync();

        Assert.Null(await new SqlAgManager().GetReplicaSeedingAsync(connection, "no-such-ag", "no-such-db", "no-such-replica"));
    }

    [Fact]
    public async Task DeadReplicaQueries_RunOnTheRealEngine()
    {
        // The queries must bind and run on a real engine; there is no AG here, so they find nothing.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlAgManager();

        Assert.Empty(await manager.GetLogsHeldByReplicasAsync(connection));
        Assert.Empty(await manager.GetAgDatabaseNamesAsync(connection, "no-such-ag"));
        await manager.DropLocalAvailabilityGroupAsync(connection, "no-such-ag");
    }

    [Fact]
    public async Task FileVolumes_AreReportedPerFile_WithTheirVolumeTotals()
    {
        // volume_mount_point is NULL on Linux; the files are mapped to their volume by path.
        await using var connection = await fixture.CreateConnectionAsync();

        var files = await new SqlAgManager().GetFileVolumesAsync(connection);

        Assert.Contains(files, f => f.PhysicalName.EndsWith("master.mdf", StringComparison.OrdinalIgnoreCase));
        Assert.All(files, f => Assert.InRange(f.AvailableBytes, 0, f.TotalBytes));
    }

    [Fact]
    public async Task InstanceUptime_IsMeasuredByTheServer()
    {
        // A freshly started instance is watched until its AG comes online.
        await using var connection = await fixture.CreateConnectionAsync();

        var uptime = await new SqlAgManager().GetInstanceUptimeAsync(connection);

        Assert.InRange(uptime, TimeSpan.FromMilliseconds(1), TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task FileSpace_IsReportedPerFileType()
    {
        // The automatic re-seed checks that the replica's volumes can hold the copy.
        await using var connection = await fixture.CreateConnectionAsync();

        var files = await new SqlAgManager().GetDatabaseFileSpaceAsync(connection, "msdb");

        Assert.Contains(files, f => f.Type == 0 && f.SizeBytes > 0 && f.AvailableBytes > 0);
        Assert.Contains(files, f => f.Type == 1 && f.SizeBytes > 0);
    }

    [Fact]
    public async Task SecondaryCopy_RestoringOutsideAnyAvailabilityGroup_IsDropped()
    {
        // SET HADR OFF only applies to a copy joined to an AG; a RESTORING copy is dropped.
        await using var connection = await fixture.CreateConnectionAsync();
        var source = $"src_{Guid.NewGuid():N}"[..20];
        var db = $"reseed_{Guid.NewGuid():N}"[..20];
        await ExecAsync(connection, $"CREATE DATABASE [{source}]");
        await ExecAsync(connection, $"BACKUP DATABASE [{source}] TO DISK = '/var/opt/mssql/data/{source}.bak' WITH FORMAT, INIT");
        await ExecAsync(connection, $"""
            RESTORE DATABASE [{db}] FROM DISK = '/var/opt/mssql/data/{source}.bak' WITH NORECOVERY,
                MOVE '{source}' TO '/var/opt/mssql/data/{db}.mdf', MOVE '{source}_log' TO '/var/opt/mssql/data/{db}_log.ldf'
            """);
        var manager = new SqlAgManager();

        await manager.DropSecondaryCopyAsync(connection, db);
        await manager.DropSecondaryCopyAsync(connection, db); // already gone: no-op

        Assert.Equal(DBNull.Value, await ScalarAsync(connection, $"SELECT DB_ID('{db}')"));
    }

    [Fact]
    public async Task SecondaryCopy_OnlineAndWritableOutsideAnyAvailabilityGroup_IsRefused()
    {
        // Such a copy may hold data of its own (restored WITH RECOVERY by hand): never dropped.
        await using var connection = await fixture.CreateConnectionAsync();
        var db = $"writable_{Guid.NewGuid():N}"[..20];
        await ExecAsync(connection, $"CREATE DATABASE [{db}]");

        var ex = await Assert.ThrowsAsync<SecondaryCopyNotDroppedException>(() => new SqlAgManager().DropSecondaryCopyAsync(connection, db));

        Assert.Equal(db, ex.Database);
        Assert.NotEqual(DBNull.Value, await ScalarAsync(connection, $"SELECT DB_ID('{db}')"));
    }

    [Fact]
    public async Task LogBackup_IsWrittenIntoItsFolder_AndOnlyBackupFilesThereAreCleaned()
    {
        // xp_create_subdir and xp_delete_file, on the engine the operator drives.
        await using var connection = await fixture.CreateConnectionAsync();
        var db = $"logbk_{Guid.NewGuid():N}"[..20];
        var folder = $"/var/opt/mssql/backup/it-{db}";
        await ExecAsync(connection, $"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET RECOVERY FULL;");
        await ExecAsync(connection, $"BACKUP DATABASE [{db}] TO DISK = N'/var/opt/mssql/backup/{db}.bak' WITH INIT");
        var manager = new SqlAgManager();

        var file = await manager.BackupLogAsync(connection, db, folder, DateTime.UtcNow);
        Assert.StartsWith(folder + "/", file);
        Assert.Equal(1, await FileExistsAsync(connection, file));

        await manager.DeleteOldBackupsAsync(connection, folder, "trn", DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, await FileExistsAsync(connection, file));
    }

    private static async Task<int> FileExistsAsync(SqlConnection connection, string path)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE #f (e int, d int, p int); INSERT #f EXEC master.dbo.xp_fileexist @path; SELECT e FROM #f;";
        cmd.Parameters.AddWithValue("@path", path);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
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

    [Fact]
    public async Task EndpointKey_WrongMasterKeyPassword_IsRejected_ServiceMasterKeyUntouched()
    {
        // The service master key used to be regenerated when the database master key could not
        // be re-bound to it. A wrong password now leaves both keys alone and says why. The reason
        // asserted here doubles as a check of the error table of validation (15313).
        await using var connection = await fixture.CreateConnectionAsync();
        var password = $"Dmk-{Guid.NewGuid():N}-1a!";
        const string cert = "p001_endpoint_cert";
        const string endpoint = "p001_endpoint";
        const string smkDate = "SELECT modify_date FROM master.sys.symmetric_keys WHERE name = '##MS_ServiceMasterKey##'";
        await ExecAsync(connection, $"""
            IF NOT EXISTS (SELECT 1 FROM master.sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
                CREATE MASTER KEY ENCRYPTION BY PASSWORD = '{password}';
            ELSE
                ALTER MASTER KEY ADD ENCRYPTION BY PASSWORD = '{password}';
            CREATE CERTIFICATE [{cert}] WITH SUBJECT = 'endpoint key test';
            CREATE ENDPOINT [{endpoint}] STATE = STARTED AS TCP (LISTENER_PORT = 5023)
                FOR DATABASE_MIRRORING (ROLE = ALL, AUTHENTICATION = CERTIFICATE [{cert}]);
            ALTER MASTER KEY DROP ENCRYPTION BY SERVICE MASTER KEY;
            """);
        var smkBefore = await ScalarAsync(connection, smkDate);
        try
        {
            await using (var probe = await fixture.CreateConnectionAsync())
            {
                var ex = await Assert.ThrowsAsync<EndpointKeyUnusableException>(() =>
                    new SqlAgManager().EnsureEndpointKeyUsableAsync(probe, endpoint, "not-the-password"));
                Assert.Equal(EndpointKeyUnusableException.MasterKeyPasswordRejected, ex.Reason);
            }
            Assert.Equal(smkBefore, await ScalarAsync(connection, smkDate));

            await using (var probe = await fixture.CreateConnectionAsync())
                Assert.NotNull(await new SqlAgManager().EnsureEndpointKeyUsableAsync(probe, endpoint, password));
            Assert.Equal(smkBefore, await ScalarAsync(connection, smkDate));
        }
        finally
        {
            await ExecAsync(connection, $"""
                BEGIN TRY
                    OPEN MASTER KEY DECRYPTION BY PASSWORD = '{password}';
                    ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY;
                    CLOSE MASTER KEY;
                END TRY
                BEGIN CATCH
                END CATCH;
                DROP ENDPOINT [{endpoint}];
                DROP CERTIFICATE [{cert}];
                """);
        }
    }
}
