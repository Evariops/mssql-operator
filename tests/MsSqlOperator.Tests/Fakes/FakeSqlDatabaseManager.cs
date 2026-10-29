using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>In-memory <see cref="ISqlDatabaseManager"/>: one database per (server, name), the server
/// being the connection's DataSource. Every write is recorded in <see cref="Calls"/>.</summary>
public sealed class FakeSqlDatabaseManager : ISqlDatabaseManager
{
    public sealed class Db
    {
        public string Collation { get; set; } = "SQL_Latin1_General_CP1_CI_AS";
        public string RecoveryModel { get; set; } = "FULL";
        public int CompatibilityLevel { get; set; } = 160;
        public string? Owner { get; set; } = "sa";
        public string State { get; set; } = "ONLINE";
        public string? OwnerTag { get; set; }
        public bool InAvailabilityGroup { get; set; }
        public long DataSize { get; set; } = 8L << 20;
        public long LogSize { get; set; } = 8L << 20;
        public long DataGrowth { get; set; } = 64L << 20;
        public long LogGrowth { get; set; } = 64L << 20;
    }

    public readonly Dictionary<(string Server, string Name), Db> Databases = [];
    public readonly List<string> Calls = [];
    public readonly HashSet<string> Logins = new(StringComparer.OrdinalIgnoreCase) { "sa" };

    public Db Add(string server, string name, Action<Db>? configure = null)
    {
        var db = new Db();
        configure?.Invoke(db);
        Databases[(server, name)] = db;
        return db;
    }

    public Db? Find(string server, string name) => Databases.GetValueOrDefault((server, name));

    private static string Server(SqlConnection c) => c.DataSource;

    private Db Get(SqlConnection c, string name)
        => Find(Server(c), name) ?? throw new InvalidOperationException($"no database {name} on {Server(c)}");

    public Task<DatabaseInfo?> GetDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default)
        => Task.FromResult(Find(Server(connection), database) is { } d
            ? new DatabaseInfo(database, d.Collation, d.RecoveryModel, d.CompatibilityLevel, d.Owner, d.State, d.InAvailabilityGroup)
            : null);

    public Task<bool> CollationExistsAsync(SqlConnection connection, string collation, CancellationToken ct = default)
        => Task.FromResult(collation != "No_Such_Collation");

    public Task CreateDatabaseAsync(SqlConnection connection, string database, string? collation, CancellationToken ct = default)
    {
        Calls.Add($"create {Server(connection)} {database} {collation}");
        Add(Server(connection), database, d => d.Collation = collation ?? d.Collation);
        return Task.CompletedTask;
    }

    public Task<string?> GetOwnerTagAsync(SqlConnection connection, string database, CancellationToken ct = default)
        => Task.FromResult(Get(connection, database).OwnerTag);

    public Task SetOwnerTagAsync(SqlConnection connection, string database, string owner, CancellationToken ct = default)
    {
        Calls.Add($"tag {Server(connection)} {database} {owner}");
        Get(connection, database).OwnerTag = owner;
        return Task.CompletedTask;
    }

    public Task SetRecoveryModelAsync(SqlConnection connection, string database, string recoveryModel, CancellationToken ct = default)
    {
        Calls.Add($"recovery {Server(connection)} {database} {recoveryModel}");
        Get(connection, database).RecoveryModel = recoveryModel;
        return Task.CompletedTask;
    }

    public Task SetCompatibilityLevelAsync(SqlConnection connection, string database, int level, CancellationToken ct = default)
    {
        Calls.Add($"compat {Server(connection)} {database} {level}");
        Get(connection, database).CompatibilityLevel = level;
        return Task.CompletedTask;
    }

    public Task<bool> LoginExistsAsync(SqlConnection connection, string login, CancellationToken ct = default)
        => Task.FromResult(Logins.Contains(login));

    public Task SetOwnerAsync(SqlConnection connection, string database, string login, CancellationToken ct = default)
    {
        Calls.Add($"owner {Server(connection)} {database} {login}");
        Get(connection, database).Owner = login;
        return Task.CompletedTask;
    }

    public Task<List<DatabaseFileInfo>> GetFilesAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        var d = Get(connection, database);
        return Task.FromResult(new List<DatabaseFileInfo>
        {
            new(database, 0, d.DataSize, d.DataGrowth, false, -1),
            new(database + "_log", 1, d.LogSize, d.LogGrowth, false, -1),
        });
    }

    public Task ModifyFileAsync(SqlConnection connection, string database, string logicalName, long? sizeBytes, long? growth,
        bool growthIsPercent, long? maxSizeBytes, CancellationToken ct = default)
    {
        Calls.Add($"file {Server(connection)} {database} {logicalName} size={sizeBytes} growth={growth}{(growthIsPercent ? "%" : "")} max={maxSizeBytes}");
        var d = Get(connection, database);
        var isLog = logicalName.EndsWith("_log", StringComparison.Ordinal);
        if (sizeBytes is { } size)
            if (isLog) d.LogSize = size; else d.DataSize = size;
        if (growth is { } g && !growthIsPercent)
            if (isLog) d.LogGrowth = g; else d.DataGrowth = g;
        return Task.CompletedTask;
    }

    public Task RenameDatabaseAsync(SqlConnection connection, string database, string newName, CancellationToken ct = default)
    {
        Calls.Add($"rename {Server(connection)} {database} {newName}");
        var db = Get(connection, database);
        Databases.Remove((Server(connection), database));
        Databases[(Server(connection), newName)] = db;
        return Task.CompletedTask;
    }

    public Task DropDatabaseAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        Calls.Add($"drop {Server(connection)} {database}");
        Databases.Remove((Server(connection), database));
        return Task.CompletedTask;
    }
}
