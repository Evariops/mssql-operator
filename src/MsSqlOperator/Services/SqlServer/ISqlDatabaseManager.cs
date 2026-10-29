using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>A user database as sys.databases describes it.</summary>
public record DatabaseInfo(string Name, string Collation, string RecoveryModel, int CompatibilityLevel, string? Owner,
    string State, bool InAvailabilityGroup);

/// <summary>A database file: sizes in bytes; <see cref="MaxSizeBytes"/> is -1 for unlimited;
/// <see cref="Growth"/> is in bytes, or in percent when <see cref="GrowthIsPercent"/>;
/// <see cref="AvailableBytes"/>: the free space of its volume, as SQL Server sees it.</summary>
public record DatabaseFileInfo(string LogicalName, int Type, long SizeBytes, long Growth, bool GrowthIsPercent, long MaxSizeBytes,
    long AvailableBytes);

/// <summary>SqlDatabase: the life of a user database on one server.</summary>
public interface ISqlDatabaseManager
{
    /// <summary>The database, or null when it does not exist.</summary>
    Task<DatabaseInfo?> GetDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default);

    /// <summary>Whether the server knows the collation.</summary>
    Task<bool> CollationExistsAsync(SqlConnection connection, string collation, CancellationToken ct = default);

    /// <summary>CREATE DATABASE, when it does not exist yet.</summary>
    Task CreateDatabaseAsync(SqlConnection connection, string database, string? collation, CancellationToken ct = default);

    /// <summary>The resource that owns the database (extended property mssqlop.owner), or null.</summary>
    Task<string?> GetOwnerTagAsync(SqlConnection connection, string database, CancellationToken ct = default);

    Task SetOwnerTagAsync(SqlConnection connection, string database, string owner, CancellationToken ct = default);

    /// <summary>FULL | SIMPLE | BULK_LOGGED.</summary>
    Task SetRecoveryModelAsync(SqlConnection connection, string database, string recoveryModel, CancellationToken ct = default);

    Task SetCompatibilityLevelAsync(SqlConnection connection, string database, int level, CancellationToken ct = default);

    Task<bool> LoginExistsAsync(SqlConnection connection, string login, CancellationToken ct = default);

    /// <summary>ALTER AUTHORIZATION ON DATABASE.</summary>
    Task SetOwnerAsync(SqlConnection connection, string database, string login, CancellationToken ct = default);

    /// <summary>The primary data file and the log file.</summary>
    Task<List<DatabaseFileInfo>> GetFilesAsync(SqlConnection connection, string database, CancellationToken ct = default);

    /// <summary>ALTER DATABASE MODIFY FILE; a null value is left as it is. <paramref name="growth"/>:
    /// bytes, or percent when <paramref name="growthIsPercent"/>; <paramref name="maxSizeBytes"/>: -1
    /// for unlimited.</summary>
    Task ModifyFileAsync(SqlConnection connection, string database, string logicalName, long? sizeBytes, long? growth,
        bool growthIsPercent, long? maxSizeBytes, CancellationToken ct = default);

    /// <summary>After a growth that failed for lack of space: gives back what the file took on disk
    /// (SQL Server keeps the former size, the file stays extended to the end of the volume), then
    /// restores <paramref name="sizeBytes"/>.</summary>
    Task ReleaseFailedGrowthAsync(SqlConnection connection, string database, string logicalName, long sizeBytes, CancellationToken ct = default);

    /// <summary>Backs the database up (COPY_ONLY) into <paramref name="folder"/>, then drops it: a
    /// copy out of the way, its content kept in the file. The path of the backup.</summary>
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DropOrphanCopy)]
    Task<string> DropOrphanCopyAsync(SqlConnection connection, string database, string folder, string fileName, CancellationToken ct = default);

    /// <summary>Drops the database, closing its sessions; a copy left RESTORING on a former
    /// secondary is dropped as it is.</summary>
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DropDatabase)]
    Task DropDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default);
}
