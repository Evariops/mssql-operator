using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlStoredProcedureManager"/>: records deploy/drop calls
/// and serves configurable existence/syntax results.</summary>
public sealed class FakeSqlStoredProcedureManager : ISqlStoredProcedureManager
{
    public readonly List<(string Db, string Schema, string Name)> DropCalls = [];
    public readonly List<(string Db, string Sql)> DeployCalls = [];
    public bool ProcedureExists { get; set; } = true;
    public bool ValidSyntax { get; set; } = true;

    public Task<bool> ProcedureExistsAsync(SqlConnection c, string database, string schema, string procedureName, CancellationToken ct = default)
        => Task.FromResult(ProcedureExists);

    public Task DeployProcedureAsync(SqlConnection c, string database, string sql, CancellationToken ct = default)
    {
        DeployCalls.Add((database, sql));
        return Task.CompletedTask;
    }

    public Task DropProcedureAsync(SqlConnection c, string database, string schema, string procedureName, CancellationToken ct = default)
    {
        DropCalls.Add((database, schema, procedureName));
        return Task.CompletedTask;
    }

    public Task<bool> ValidateSyntaxAsync(SqlConnection c, string database, string sql, CancellationToken ct = default) => Task.FromResult(ValidSyntax);

    public Task DeployInTransactionAsync(SqlConnection c, string database, List<string> sqlStatements, CancellationToken ct = default)
    {
        foreach (var sql in sqlStatements) DeployCalls.Add((database, sql));
        return Task.CompletedTask;
    }

    public string ComputeHash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public (string schema, string name)? ParseProcedureName(string sql) => null;
}
