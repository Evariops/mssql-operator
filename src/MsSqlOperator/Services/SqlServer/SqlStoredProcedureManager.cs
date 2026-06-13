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
            SELECT COUNT(*) FROM {SqlQuoting.QuoteIdentifier(database)}.sys.procedures p
            JOIN {SqlQuoting.QuoteIdentifier(database)}.sys.schemas s ON p.schema_id = s.schema_id
            WHERE p.name = @name AND s.name = @schema
            """;
        cmd.Parameters.AddWithValue("@name", procedureName);
        cmd.Parameters.AddWithValue("@schema", schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task DeployProcedureAsync(SqlConnection connection, string database, string sql, CancellationToken ct = default)
    {
        // CREATE/ALTER PROCEDURE must be the FIRST statement in its batch, so the database
        // switch has to run in a separate batch — prepending "USE [db];" makes SQL Server
        // reject the proc with "CREATE/ALTER PROCEDURE must be the first statement in a
        // query batch". USE persists for the session, so the next command runs in [database].
        await SwitchDatabaseAsync(connection, database, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropProcedureAsync(SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; DROP PROCEDURE IF EXISTS {SqlQuoting.QuoteIdentifier(schema)}.{SqlQuoting.QuoteIdentifier(procedureName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Switches the connection's current database in its own batch. Used before a
    // CREATE/ALTER PROCEDURE so the proc statement is first in the next batch.
    private static async Task SwitchDatabaseAsync(SqlConnection connection, string database, CancellationToken ct)
    {
        await using var useCmd = connection.CreateCommand();
        useCmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}";
        await useCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ValidateSyntaxAsync(SqlConnection connection, string database, string sql, CancellationToken ct = default)
    {
        // SET PARSEONLY parses without executing, but the proc statement must still be the
        // first statement in its batch. So switch DB, enable PARSEONLY, then run the proc
        // SQL alone — each in its own batch. PARSEONLY is always turned back off in the
        // finally so the connection is not left poisoned for the subsequent deploy.
        try
        {
            await SwitchDatabaseAsync(connection, database, ct);
            await using (var on = connection.CreateCommand())
            {
                on.CommandText = "SET PARSEONLY ON";
                await on.ExecuteNonQueryAsync(ct);
            }
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                await using var off = connection.CreateCommand();
                off.CommandText = "SET PARSEONLY OFF";
                await off.ExecuteNonQueryAsync(ct);
            }
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
        useCmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}";
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
