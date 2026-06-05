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

    public async Task ApplySpConfigureAsync(SqlConnection connection, string name, int value, CancellationToken ct = default)
    {
        await using var showAdvanced = connection.CreateCommand();
        showAdvanced.CommandText = "EXEC sp_configure 'show advanced options', 1; RECONFIGURE;";
        await showAdvanced.ExecuteNonQueryAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"EXEC sp_configure @name, @value";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", value);
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
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetInt32(reader.GetOrdinal("Status")) == 1)
            {
                flags.Add(reader.GetInt32(reader.GetOrdinal("TraceFlag")));
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
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetDatabaseNamesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        var databases = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            databases.Add(reader.GetString(0));
        }
        return databases;
    }
}
