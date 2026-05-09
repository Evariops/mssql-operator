using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public partial class SqlStoredProcedureManager : ISqlStoredProcedureManager
{
    [GeneratedRegex(@"CREATE\s+(?:OR\s+ALTER\s+)?PROCEDURE\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ProcedureNameRegex();

    public async Task<bool> ProcedureExistsAsync(SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM [{database}].sys.procedures p
            JOIN [{database}].sys.schemas s ON p.schema_id = s.schema_id
            WHERE p.name = @name AND s.name = @schema
            """;
        cmd.Parameters.AddWithValue("@name", procedureName);
        cmd.Parameters.AddWithValue("@schema", schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task DeployProcedureAsync(SqlConnection connection, string database, string sql, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; {sql}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropProcedureAsync(SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; DROP PROCEDURE IF EXISTS [{schema}].[{procedureName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ValidateSyntaxAsync(SqlConnection connection, string database, string sql, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"USE [{database}]; SET PARSEONLY ON; {sql}; SET PARSEONLY OFF;";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task DeployInTransactionAsync(SqlConnection connection, string database, List<string> sqlStatements, CancellationToken ct = default)
    {
        await using var useCmd = connection.CreateCommand();
        useCmd.CommandText = $"USE [{database}]";
        await useCmd.ExecuteNonQueryAsync(ct);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var sql in sqlStatements)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public string ComputeHash(string content)
    {
        var normalized = content.Trim().ReplaceLineEndings("\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    public (string schema, string name)? ParseProcedureName(string sql)
    {
        var match = ProcedureNameRegex().Match(sql);
        if (!match.Success) return null;
        var schema = match.Groups[1].Success ? match.Groups[1].Value : "dbo";
        var name = match.Groups[2].Value;
        return (schema, name);
    }
}
