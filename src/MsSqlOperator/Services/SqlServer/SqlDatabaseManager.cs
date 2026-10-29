using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public partial class SqlDatabaseManager : ISqlDatabaseManager
{
    /// <summary>The extended property naming the resource that owns a database. It lives in the
    /// database: it follows it through a failover, and through a re-creation of the resource.</summary>
    public const string OwnerProperty = "mssqlop.owner";

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex CollationName();

    public async Task<DatabaseInfo?> GetDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT name, collation_name, recovery_model_desc, compatibility_level, SUSER_SNAME(owner_sid), state_desc,
                   CAST(CASE WHEN replica_id IS NULL THEN 0 ELSE 1 END AS bit)
            FROM sys.databases WHERE name = @db
            """;
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new DatabaseInfo(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2),
            reader.GetByte(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetBoolean(6));
    }

    public async Task<bool> CollationExistsAsync(SqlConnection connection, string collation, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.fn_helpcollations() WHERE name = @c";
        cmd.Parameters.AddWithValue("@c", collation);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task CreateDatabaseAsync(SqlConnection connection, string database, string? collation, CancellationToken ct = default)
    {
        // A collation is a keyword, not an identifier: it cannot be quoted, so it is checked instead.
        if (collation is not null && !CollationName().IsMatch(collation))
            throw new ArgumentException($"Not a collation name: {collation}", nameof(collation));
        await using var cmd = connection.CreateCommand();
        var db = SqlQuoting.QuoteIdentifier(database);
        var collate = collation is null ? "" : " COLLATE " + collation;
        cmd.CommandText = $"IF DB_ID(@db) IS NULL CREATE DATABASE {db}{collate};";
        cmd.Parameters.AddWithValue("@db", database);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<string?> GetOwnerTagAsync(SqlConnection connection, string database, CancellationToken ct = default)
        => DatabaseProperties.ReadAsync(connection, database, OwnerProperty, ct);

    public Task SetOwnerTagAsync(SqlConnection connection, string database, string owner, CancellationToken ct = default)
        => DatabaseProperties.WriteAsync(connection, database, OwnerProperty, owner, ct);

    public async Task<bool> HasUserObjectsAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SqlQuoting.QuoteIdentifier(database)}.sys.objects WHERE is_ms_shipped = 0";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task SetRecoveryModelAsync(SqlConnection connection, string database, string recoveryModel, CancellationToken ct = default)
    {
        var model = recoveryModel.ToUpperInvariant() switch
        {
            "FULL" => "FULL",
            "SIMPLE" => "SIMPLE",
            "BULK_LOGGED" or "BULKLOGGED" => "BULK_LOGGED",
            _ => throw new ArgumentException($"Not a recovery model: {recoveryModel}", nameof(recoveryModel)),
        };
        await ExecuteAsync(connection, $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET RECOVERY {model};", ct);
    }

    public Task SetCompatibilityLevelAsync(SqlConnection connection, string database, int level, CancellationToken ct = default)
        => ExecuteAsync(connection,
            $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET COMPATIBILITY_LEVEL = {level.ToString(CultureInfo.InvariantCulture)};", ct);

    public async Task<bool> LoginExistsAsync(SqlConnection connection, string login, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.server_principals WHERE name = @l";
        cmd.Parameters.AddWithValue("@l", login);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public Task SetOwnerAsync(SqlConnection connection, string database, string login, CancellationToken ct = default)
        => ExecuteAsync(connection,
            $"ALTER AUTHORIZATION ON DATABASE::{SqlQuoting.QuoteIdentifier(database)} TO {SqlQuoting.QuoteIdentifier(login)};", ct);

    public async Task<List<DatabaseFileInfo>> GetFilesAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // The primary data file (file_id 1) and the first log file: the ones a SqlDatabase sizes.
        cmd.CommandText = $"""
            SELECT f.name, f.type, CAST(f.size AS bigint) * 8192, CAST(f.growth AS bigint), f.is_percent_growth, CAST(f.max_size AS bigint),
                   CAST(vs.available_bytes AS bigint)
            FROM {SqlQuoting.QuoteIdentifier(database)}.sys.database_files f
            CROSS APPLY sys.dm_os_volume_stats(DB_ID(@db), f.file_id) vs
            WHERE f.file_id = 1 OR f.file_id = (SELECT MIN(file_id) FROM {SqlQuoting.QuoteIdentifier(database)}.sys.database_files WHERE type = 1)
            """;
        cmd.Parameters.AddWithValue("@db", database);
        var files = new List<DatabaseFileInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var percent = reader.GetBoolean(4);
            var growth = reader.GetInt64(3);
            var maxPages = reader.GetInt64(5);
            files.Add(new DatabaseFileInfo(reader.GetString(0), reader.GetByte(1), reader.GetInt64(2),
                percent ? growth : growth * 8192, percent, maxPages is -1 or 268435456 ? -1 : maxPages * 8192, reader.GetInt64(6)));
        }
        return files;
    }

    public async Task ModifyFileAsync(SqlConnection connection, string database, string logicalName, long? sizeBytes, long? growth,
        bool growthIsPercent, long? maxSizeBytes, CancellationToken ct = default)
    {
        // The size last: a growth or a maximum is not held back by a size the disk refuses.
        var options = new List<string>();
        if (growth is { } g)
            options.Add(growthIsPercent ? $"FILEGROWTH = {g.ToString(CultureInfo.InvariantCulture)}%" : $"FILEGROWTH = {Kb(g)}KB");
        if (maxSizeBytes is { } max)
            options.Add(max < 0 ? "MAXSIZE = UNLIMITED" : $"MAXSIZE = {Kb(max)}KB");
        if (sizeBytes is { } size)
            options.Add($"SIZE = {Kb(size)}KB");
        if (options.Count == 0)
            return;
        // SQL Server refuses SIZE when it equals the current size, and each option alone is valid:
        // one statement per option keeps a no-op from failing the others.
        foreach (var option in options)
            await ExecuteAsync(connection,
                $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} MODIFY FILE (NAME = {SqlQuoting.QuoteIdentifier(logicalName)}, {option});", ct);
    }

    public async Task ReleaseFailedGrowthAsync(SqlConnection connection, string database, string logicalName, long sizeBytes,
        CancellationToken ct = default)
    {
        var db = SqlQuoting.QuoteIdentifier(database);
        await using (var shrink = connection.CreateCommand())
        {
            shrink.CommandText = $"EXEC({SqlQuoting.QuoteNStringLiteral($"USE {db}; DBCC SHRINKFILE ({SqlQuoting.QuoteNStringLiteral(logicalName)}, TRUNCATEONLY) WITH NO_INFOMSGS;")});";
            shrink.CommandTimeout = 120;
            await shrink.ExecuteNonQueryAsync(ct);
        }
        // TRUNCATEONLY may go below the former size: the file gets it back.
        var current = (await GetFilesAsync(connection, database, ct)).FirstOrDefault(f => f.LogicalName == logicalName);
        if (current is not null && current.SizeBytes < sizeBytes)
            await ExecuteAsync(connection, $"ALTER DATABASE {db} MODIFY FILE (NAME = {SqlQuoting.QuoteIdentifier(logicalName)}, SIZE = {Kb(sizeBytes)}KB);", ct);
    }

    public async Task<string> DropOrphanCopyAsync(SqlConnection connection, string database, string folder, string fileName,
        CancellationToken ct = default)
    {
        var path = $"{folder.TrimEnd('/')}/{fileName}";
        await using (var backup = connection.CreateCommand())
        {
            backup.CommandText = $"EXEC master.dbo.xp_create_subdir @folder; BACKUP DATABASE {SqlQuoting.QuoteIdentifier(database)} TO DISK = @path WITH COPY_ONLY, COMPRESSION;";
            backup.Parameters.AddWithValue("@folder", folder);
            backup.Parameters.AddWithValue("@path", path);
            backup.CommandTimeout = 600;
            await backup.ExecuteNonQueryAsync(ct);
        }
        await DropDatabaseAsync(connection, database, ct);
        return path;
    }

    public async Task DropDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        var db = SqlQuoting.QuoteIdentifier(database);
        // A copy left RESTORING on a former secondary accepts no ALTER (nor does a read-only one,
        // whose lock request fails while it leaves the AG): it is dropped as it is.
        cmd.CommandText = $"""
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                IF DATABASEPROPERTYEX(@db, 'Status') = 'ONLINE' AND DATABASEPROPERTYEX(@db, 'Updateability') = 'READ_WRITE'
                    ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {db};
            END
            """;
        cmd.Parameters.AddWithValue("@db", database);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static long Kb(long bytes) => Math.Max(1, (bytes + 1023) / 1024);

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
