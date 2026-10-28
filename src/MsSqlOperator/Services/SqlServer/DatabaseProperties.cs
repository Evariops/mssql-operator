using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Database-level extended properties (class 0): state kept in the database itself, so it follows
/// the database through a failover and outlives the CR status — the divergence markers,
/// the DEK regeneration owed. Written on the primary; replicated with the database.
/// </summary>
internal static class DatabaseProperties
{
    public static async Task<string?> ReadAsync(SqlConnection connection, string database, string name, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT CAST(value AS nvarchar(4000)) FROM {SqlQuoting.QuoteIdentifier(database)}.sys.extended_properties " +
                          "WHERE class = 0 AND name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Sets the property, or removes it when <paramref name="value"/> is null.</summary>
    public static async Task WriteAsync(SqlConnection connection, string database, string name, string? value, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        var db = SqlQuoting.QuoteIdentifier(database);
        cmd.CommandText = $"""
            IF EXISTS (SELECT 1 FROM {db}.sys.extended_properties WHERE class = 0 AND name = @name)
                EXEC {db}.sys.sp_dropextendedproperty @name = @name;
            IF @value IS NOT NULL
                EXEC {db}.sys.sp_addextendedproperty @name = @name, @value = @value;
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
