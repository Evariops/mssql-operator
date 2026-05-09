namespace MsSqlOperator.Services.SqlServer;

public interface ISqlServerManager
{
    Task<Dictionary<string, int>> GetCurrentConfigAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task ApplySpConfigureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string name, int value, CancellationToken ct = default);
    Task ReconfigureAsync(Microsoft.Data.SqlClient.SqlConnection connection, bool withOverride = false, CancellationToken ct = default);
    Task<List<int>> GetActiveTraceFlagsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task EnableTraceFlagAsync(Microsoft.Data.SqlClient.SqlConnection connection, int flag, CancellationToken ct = default);
    Task DisableTraceFlagAsync(Microsoft.Data.SqlClient.SqlConnection connection, int flag, CancellationToken ct = default);
    Task<int> GetTempDbFileCountAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task AddTempDbFileAsync(Microsoft.Data.SqlClient.SqlConnection connection, int initialSizeMb, int autoGrowthMb, CancellationToken ct = default);
    Task<bool> IsHealthyAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<List<string>> GetDatabaseNamesAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
}
