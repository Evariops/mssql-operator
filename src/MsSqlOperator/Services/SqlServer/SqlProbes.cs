using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>The small state reads the controllers and the target resolver made with their
/// own commands; SQL text lives in the managers.</summary>
public static class SqlProbes
{
    /// <summary>SERVERPROPERTY('ProductVersion'): what build answers.</summary>
    public static async Task<string?> GetProductVersionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductVersion'))";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Whether the server behind <paramref name="connection"/> holds the primary role of an AG.</summary>
    public static async Task<bool> IsLocalPrimaryAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sys.dm_hadr_availability_replica_states WHERE role_desc = 'PRIMARY' AND is_local = 1";
        return await cmd.ExecuteScalarAsync(ct) != null;
    }
}
