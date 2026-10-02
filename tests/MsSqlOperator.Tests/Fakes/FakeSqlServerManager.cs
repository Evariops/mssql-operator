using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlServerManager"/>: serves a configurable database
/// list (the only query the finalizers/controllers need here); other members are inert stubs.</summary>
public sealed class FakeSqlServerManager : ISqlServerManager
{
    public List<string> DatabaseNames { get; set; } = [];
    public bool Healthy { get; set; } = true;

    public Task<List<string>> GetDatabaseNamesAsync(SqlConnection connection, CancellationToken ct = default) => Task.FromResult(DatabaseNames);
    public Task<bool> IsHealthyAsync(SqlConnection connection, CancellationToken ct = default) => Task.FromResult(Healthy);

    // ── Inert stubs ──────────────────────────────────────────────────────
    public Task<Dictionary<string, int>> GetCurrentConfigAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, int>());
    public Task<Dictionary<string, int>> GetConfiguredValuesAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, int>());
    public Task<(string RecoveryModel, int? GrowthMb, string LogicalName)?> GetModelDatabaseSettingsAsync(SqlConnection c, CancellationToken ct = default)
        => Task.FromResult<(string, int?, string)?>(("SIMPLE", 64, "modeldev"));
    public Task SetModelRecoveryModelAsync(SqlConnection c, string recoveryModel, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetModelFileGrowthAsync(SqlConnection c, string logicalName, int growthMb, CancellationToken ct = default) => Task.CompletedTask;
    public Task ApplySpConfigureAsync(SqlConnection c, string name, int value, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReconfigureAsync(SqlConnection c, bool withOverride = false, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<int>> GetActiveTraceFlagsAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(new List<int>());
    public Task EnableTraceFlagAsync(SqlConnection c, int flag, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisableTraceFlagAsync(SqlConnection c, int flag, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetTempDbFileCountAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(0);
    public Task AddTempDbFileAsync(SqlConnection c, int initialSizeMb, int autoGrowthMb, string tempDbPath = "/var/opt/mssql/data", CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<(string Name, string FileName)>> GetTempDbFileLocationsAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(new List<(string, string)>());
    public Task RelocateTempDbFileAsync(SqlConnection c, string logicalName, string newFilePath, CancellationToken ct = default) => Task.CompletedTask;
}
