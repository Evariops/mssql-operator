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

    public async Task DeployProcedureAsync(SqlConnection connection, string database, ProcedureDefinition procedure, CancellationToken ct = default)
    {
        await SwitchDatabaseAsync(connection, database, ct);
        await ExecuteAsync(connection, null, procedure.SettingsBatch, ct);
        await ExecuteAsync(connection, null, procedure.Batch, ct);
    }

    public async Task<bool> ValidateSyntaxAsync(SqlConnection connection, string database, ProcedureDefinition procedure, CancellationToken ct = default)
    {
        // QUOTED_IDENTIFIER changes how "…" parses: state it before PARSEONLY.
        await ExecuteAsync(connection, null, procedure.SettingsBatch, ct);
        return await ValidateSyntaxAsync(connection, database, procedure.Batch, ct);
    }

    public async Task DeployInTransactionAsync(SqlConnection connection, string database, IReadOnlyList<ProcedureDefinition> procedures, CancellationToken ct = default)
    {
        await SwitchDatabaseAsync(connection, database, ct);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var procedure in procedures)
            {
                await ExecuteAsync(connection, transaction, procedure.SettingsBatch, ct);
                await ExecuteAsync(connection, transaction, procedure.Batch, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<HashSet<(string Database, string Schema, string Name)>> GetProceduresAsync(SqlConnection connection,
        IReadOnlyCollection<string> databases, CancellationToken ct = default)
    {
        var result = new HashSet<(string, string, string)>(ProcedureKeyComparer.Instance);
        if (databases.Count == 0)
            return result;

        // One round-trip for every database, instead of one query per database and procedure.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.Join("\nUNION ALL\n", databases.Select(db =>
        {
            var qdb = SqlQuoting.QuoteIdentifier(db);
            return $"SELECT {SqlQuoting.QuoteNStringLiteral(db)}, s.name, p.name FROM {qdb}.sys.procedures p " +
                   $"JOIN {qdb}.sys.schemas s ON s.schema_id = p.schema_id";
        }));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    /// <summary>Database, schema and procedure names compare case-insensitively, as SQL Server's
    /// default collation does.</summary>
    private sealed class ProcedureKeyComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly ProcedureKeyComparer Instance = new();

        public bool Equals((string, string, string) x, (string, string, string) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Item3, y.Item3);

        public int GetHashCode((string, string, string) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3));
    }

    public async Task<List<ProcedureDeploymentState>> GetDeploymentStatesAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        // OBJECT_DEFINITION resolves in the current database: run in the target one through its own
        // sp_executesql instead of switching the session.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"EXEC {SqlQuoting.QuoteIdentifier(database)}.sys.sp_executesql @stmt, N'@prop sysname', @prop = @prop";
        cmd.Parameters.AddWithValue("@stmt", """
            SELECT s.name, p.name, CAST(ep.value AS nvarchar(4000)),
                   CONVERT(varchar(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(p.object_id)), 2)
            FROM sys.procedures p
            JOIN sys.schemas s ON s.schema_id = p.schema_id
            LEFT JOIN sys.extended_properties ep
                ON ep.class = 1 AND ep.major_id = p.object_id AND ep.minor_id = 0 AND ep.name = @prop
            """);
        cmd.Parameters.AddWithValue("@prop", ProcedureMarker.PropertyName);

        var states = new List<ProcedureDeploymentState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            states.Add(new ProcedureDeploymentState(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return states;
    }

    public async Task MarkDeployedAsync(SqlConnection connection, string database, string schema, string procedureName, string owner, string specHash, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"EXEC {SqlQuoting.QuoteIdentifier(database)}.sys.sp_executesql @stmt, " +
                          "N'@schema sysname, @name sysname, @prop sysname, @prefix nvarchar(3000)', " +
                          "@schema = @schema, @name = @name, @prop = @prop, @prefix = @prefix";
        cmd.Parameters.AddWithValue("@stmt", """
            DECLARE @id int = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name));
            DECLARE @value nvarchar(4000) = @prefix
                + ISNULL(CONVERT(varchar(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(@id)), 2), '');
            IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND major_id = @id AND minor_id = 0 AND name = @prop)
                EXEC sys.sp_updateextendedproperty @name = @prop, @value = @value,
                    @level0type = N'SCHEMA', @level0name = @schema, @level1type = N'PROCEDURE', @level1name = @name;
            ELSE
                EXEC sys.sp_addextendedproperty @name = @prop, @value = @value,
                    @level0type = N'SCHEMA', @level0name = @schema, @level1type = N'PROCEDURE', @level1name = @name;
            """);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", procedureName);
        cmd.Parameters.AddWithValue("@prop", ProcedureMarker.PropertyName);
        cmd.Parameters.AddWithValue("@prefix", ProcedureMarker.Prefix(owner, specHash));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
