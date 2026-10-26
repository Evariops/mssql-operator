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

    /// <summary>The procedures the batched listing returns; empty unless configured.</summary>
    public HashSet<(string Database, string Schema, string Name)> Procedures { get; set; } = [];
    public Task<HashSet<(string Database, string Schema, string Name)>> GetProceduresAsync(SqlConnection c,
        IReadOnlyCollection<string> databases, CancellationToken ct = default)
        => Task.FromResult(Procedures.Where(p => databases.Contains(p.Database)).ToHashSet());

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

    public Task DeployProcedureAsync(SqlConnection c, string database, ProcedureDefinition procedure, CancellationToken ct = default)
    {
        DeployCalls.Add((database, procedure.Batch));
        return Task.CompletedTask;
    }

    public Task<bool> ValidateSyntaxAsync(SqlConnection c, string database, ProcedureDefinition procedure, CancellationToken ct = default)
        => Task.FromResult(ValidSyntax);

    public Task DeployInTransactionAsync(SqlConnection c, string database, IReadOnlyList<ProcedureDefinition> procedures, CancellationToken ct = default)
    {
        foreach (var procedure in procedures) DeployCalls.Add((database, procedure.Batch));
        return Task.CompletedTask;
    }

    /// <summary>Procedures present per database, as GetDeploymentStatesAsync reports them.</summary>
    public Dictionary<string, List<ProcedureDeploymentState>> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Db, string Schema, string Name, string Owner, string SpecHash)> MarkCalls = [];

    public Task<List<ProcedureDeploymentState>> GetDeploymentStatesAsync(SqlConnection c, string database, CancellationToken ct = default)
        => Task.FromResult(States.TryGetValue(database, out var states) ? states.ToList() : []);

    public Task MarkDeployedAsync(SqlConnection c, string database, string schema, string procedureName, string owner, string specHash, CancellationToken ct = default)
    {
        MarkCalls.Add((database, schema, procedureName, owner, specHash));
        return Task.CompletedTask;
    }

    public string ComputeHash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public (string schema, string name)? ParseProcedureName(string sql) => null;
}
