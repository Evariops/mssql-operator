namespace MsSqlOperator.Services.SqlServer;

public interface ISqlServerManager
{
    Task<Dictionary<string, int>> GetCurrentConfigAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    /// <summary>sys.configurations.value — what is configured, applied or still pending a
    /// RECONFIGURE/restart, where <see cref="GetCurrentConfigAsync"/> reads value_in_use.</summary>
    Task<Dictionary<string, int>> GetConfiguredValuesAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    /// <summary>Recovery model and data-file growth (MB, null for a percentage) of the model database.</summary>
    Task<(string RecoveryModel, int? GrowthMb, string LogicalName)?> GetModelDatabaseSettingsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task SetModelRecoveryModelAsync(Microsoft.Data.SqlClient.SqlConnection connection, string recoveryModel, CancellationToken ct = default);
    Task SetModelFileGrowthAsync(Microsoft.Data.SqlClient.SqlConnection connection, string logicalName, int growthMb, CancellationToken ct = default);
    Task ApplySpConfigureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string name, int value, CancellationToken ct = default);
    Task ReconfigureAsync(Microsoft.Data.SqlClient.SqlConnection connection, bool withOverride = false, CancellationToken ct = default);
    Task<List<int>> GetActiveTraceFlagsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task EnableTraceFlagAsync(Microsoft.Data.SqlClient.SqlConnection connection, int flag, CancellationToken ct = default);
    Task DisableTraceFlagAsync(Microsoft.Data.SqlClient.SqlConnection connection, int flag, CancellationToken ct = default);
    Task<int> GetTempDbFileCountAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task AddTempDbFileAsync(Microsoft.Data.SqlClient.SqlConnection connection, int initialSizeMb, int autoGrowthMb, string tempDbPath = "/var/opt/mssql/data", CancellationToken ct = default);
    Task<List<(string Name, string FileName)>> GetTempDbFileLocationsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task RelocateTempDbFileAsync(Microsoft.Data.SqlClient.SqlConnection connection, string logicalName, string newFilePath, CancellationToken ct = default);
    Task<bool> IsHealthyAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<List<string>> GetDatabaseNamesAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
}
