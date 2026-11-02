using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.IntegrationTests;

/// <summary> on a real SQL Server: the preStop CHECKPOINT reaches the user databases.</summary>
[Collection(SqlServerCollection.Name)]
public class PreStopCheckpointTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task PreStopScript_ChecksPointsAUserDatabase_AndSkipsAReadOnlyOne()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var writable = $"prestop_rw_{suffix}";
        var readOnly = $"prestop_ro_{suffix}";
        await using var connection = await fixture.CreateConnectionAsync();
        await ExecAsync(connection, $"CREATE DATABASE [{writable}]; CREATE DATABASE [{readOnly}];");
        await ExecAsync(connection, $"ALTER DATABASE [{readOnly}] SET READ_ONLY;");
        await ExecAsync(connection,
            $"USE [{writable}]; CREATE TABLE t (id int IDENTITY PRIMARY KEY, pad char(2000) NOT NULL DEFAULT 'x'); " +
            "INSERT INTO t DEFAULT VALUES; WHILE (SELECT COUNT(*) FROM t) < 2000 INSERT INTO t (pad) SELECT pad FROM t;");
        Assert.True(await DirtyPagesAsync(connection, writable) > 0);

        // The hook exactly as the pod runs it, minus the SIGTERM: sqlcmd from the image, the password
        // fallback to MSSQL_SA_PASSWORD, and the shell quoting of the query.
        var script = StatefulSetBuilder.SqlcmdShell(
            $"-l {StatefulSetBuilder.PreStopLoginSeconds} -t {StatefulSetBuilder.PreStopCheckpointSeconds} -b",
            StatefulSetBuilder.PreStopCheckpointQuery);
        var result = await fixture.ExecAsync(["/bin/sh", "-c", script]);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal(0, await DirtyPagesAsync(connection, writable));
    }

    private static async Task ExecAsync(Microsoft.Data.SqlClient.SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> DirtyPagesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_os_buffer_descriptors WHERE database_id = DB_ID(@db) AND is_modified = 1";
        cmd.Parameters.AddWithValue("@db", database);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
