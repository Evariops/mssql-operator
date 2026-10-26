using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlServerManager : ISqlServerManager
{
    public async Task<Dictionary<string, int>> GetCurrentConfigAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var config = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, CAST(value_in_use AS INT) FROM sys.configurations";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            config[reader.GetString(0)] = reader.GetInt32(1);
        }
        return config;
    }

    public async Task<Dictionary<string, int>> GetConfiguredValuesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var config = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, CAST(value AS INT) FROM sys.configurations";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            config[reader.GetString(0)] = reader.GetInt32(1);
        return config;
    }

    /// <summary>
    /// Sets an sp_configure value; the caller commits it with RECONFIGURE. 'show advanced
    /// options' is raised only when it is off, and put back to off (committed by the caller's
    /// RECONFIGURE) — it used to be left at 1 for good on every instance the operator touched.
    /// </summary>
    public async Task ApplySpConfigureAsync(SqlConnection connection, string name, int value, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DECLARE @advanced int = (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'show advanced options');
            IF @advanced = 0
            BEGIN
                EXEC sp_configure 'show advanced options', 1;
                RECONFIGURE;
            END
            EXEC sp_configure @name, @value;
            IF @advanced = 0 AND @name <> 'show advanced options'
                EXEC sp_configure 'show advanced options', 0;
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string RecoveryModel, int? GrowthMb, string LogicalName)?> GetModelDatabaseSettingsAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.recovery_model_desc, mf.growth, mf.is_percent_growth, mf.name
            FROM sys.databases d
            JOIN sys.master_files mf ON mf.database_id = d.database_id AND mf.type = 0
            WHERE d.name = 'model'
            ORDER BY mf.file_id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        // growth is in 8 KB pages unless it is a percentage.
        int? growthMb = reader.GetBoolean(2) ? null : reader.GetInt32(1) * 8 / 1024;
        return (reader.GetString(0), growthMb, reader.GetString(3));
    }

    public async Task SetModelRecoveryModelAsync(SqlConnection connection, string recoveryModel, CancellationToken ct = default)
    {
        var model = recoveryModel.ToUpperInvariant() switch
        {
            "SIMPLE" => "SIMPLE",
            "FULL" => "FULL",
            "BULKLOGGED" or "BULK_LOGGED" => "BULK_LOGGED",
            _ => throw new ArgumentException($"Unknown recovery model \"{recoveryModel}\" (expected Simple, Full or BulkLogged)."),
        };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [model] SET RECOVERY {model}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetModelFileGrowthAsync(SqlConnection connection, string logicalName, int growthMb, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [model] MODIFY FILE (NAME = {SqlQuoting.QuoteNStringLiteral(logicalName)}, FILEGROWTH = {growthMb}MB)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReconfigureAsync(SqlConnection connection, bool withOverride = false, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = withOverride ? "RECONFIGURE WITH OVERRIDE" : "RECONFIGURE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<int>> GetActiveTraceFlagsAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var flags = new List<int>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DBCC TRACESTATUS(-1)";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        // With no active flags, DBCC TRACESTATUS(-1) returns an empty result set with no
        // column metadata, so the ordinals must be resolved inside the loop (which never runs
        // in that case) rather than up front. The columns are smallint (Int16), so GetInt32
        // throws — read type-agnostically and convert.
        while (await reader.ReadAsync(ct))
        {
            if (Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Status"))) == 1)
            {
                flags.Add(Convert.ToInt32(reader.GetValue(reader.GetOrdinal("TraceFlag"))));
            }
        }
        return flags;
    }

    public async Task EnableTraceFlagAsync(SqlConnection connection, int flag, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DBCC TRACEON({flag}, -1)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DisableTraceFlagAsync(SqlConnection connection, int flag, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DBCC TRACEOFF({flag}, -1)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetTempDbFileCountAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tempdb.sys.database_files WHERE type = 0";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task AddTempDbFileAsync(SqlConnection connection, int initialSizeMb, int autoGrowthMb, string tempDbPath = "/var/opt/mssql/data", CancellationToken ct = default)
    {
        var fileCount = await GetTempDbFileCountAsync(connection, ct);
        var fileName = $"tempdev{fileCount}";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            ALTER DATABASE tempdb ADD FILE (
                NAME = {SqlQuoting.QuoteIdentifier(fileName)},
                FILENAME = {SqlQuoting.QuoteStringLiteral($"{tempDbPath}/{fileName}.ndf")},
                SIZE = {initialSizeMb}MB,
                FILEGROWTH = {autoGrowthMb}MB
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(string Name, string FileName)>> GetTempDbFileLocationsAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var files = new List<(string Name, string FileName)>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, physical_name FROM tempdb.sys.database_files";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            files.Add((reader.GetString(0), reader.GetString(1)));
        }
        return files;
    }

    public async Task RelocateTempDbFileAsync(SqlConnection connection, string logicalName, string newFilePath, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            ALTER DATABASE tempdb MODIFY FILE (
                NAME = {SqlQuoting.QuoteIdentifier(logicalName)},
                FILENAME = {SqlQuoting.QuoteStringLiteral(newFilePath)}
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsHealthyAsync(SqlConnection connection, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result) == 1;
        }
        // A SQL or connection-state error means "not responding"; a cancellation is not one.
        catch (Exception e) when (e is SqlException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<List<string>> GetDatabaseNamesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var databases = new List<string>();
        await using var cmd = connection.CreateCommand();
        // Only writable databases — DDL against read-only AG secondaries fails
        // forever and used to wedge deletions/permission grants.
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND DATABASEPROPERTYEX(name, 'Updateability') = 'READ_WRITE' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            databases.Add(reader.GetString(0));
        }
        return databases;
    }
}
