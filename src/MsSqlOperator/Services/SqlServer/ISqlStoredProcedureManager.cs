namespace MsSqlOperator.Services.SqlServer;

/// <summary>A procedure as found in a database: its deployment marker (<see cref="ProcedureMarker"/>)
/// and the hash of its current definition, computed by SQL Server.</summary>
public sealed record ProcedureDeploymentState(string Schema, string Name, string? Marker, string? DefinitionHash);

public interface ISqlStoredProcedureManager
{
    Task<bool> ProcedureExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default);
    Task DeployProcedureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string sql, CancellationToken ct = default);
    /// <summary>Deploys one parsed definition: its SET batch, then its CREATE batch, each alone.</summary>
    Task DeployProcedureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, ProcedureDefinition procedure, CancellationToken ct = default);
    Task DropProcedureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default);
    Task<bool> ValidateSyntaxAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string sql, CancellationToken ct = default);
    Task<bool> ValidateSyntaxAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, ProcedureDefinition procedure, CancellationToken ct = default);
    Task DeployInTransactionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, List<string> sqlStatements, CancellationToken ct = default);
    /// <summary>
    /// Deploys each procedure in its own transaction, under the DDL session settings
    /// (LOCK_TIMEOUT 3000, DEADLOCK_PRIORITY LOW, XACT_ABORT ON). One transaction for every procedure
    /// held the schema locks of all of them until the last was compiled, and an ALTER waiting on an
    /// application lock queued every caller of the procedure behind it. Returns the procedures
    /// deferred because another session held them (lock timeout, deadlock): the next pass retries them.
    /// </summary>
    Task<IReadOnlyList<ProcedureDefinition>> DeployInTransactionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database,
        IReadOnlyList<ProcedureDefinition> procedures, CancellationToken ct = default);

    /// <summary>The procedures of every database in <paramref name="databases"/>, in one round-trip
    /// Names compare case-insensitively.</summary>
    Task<HashSet<(string Database, string Schema, string Name)>> GetProceduresAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        IReadOnlyCollection<string> databases, CancellationToken ct = default);

    /// <summary>Every procedure of <paramref name="database"/> with its deployment marker and the
    /// SHA-256 of its current definition.</summary>
    Task<List<ProcedureDeploymentState>> GetDeploymentStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);

    /// <summary>Stamps a deployed procedure with its marker: owner, content hash, and the hash of the
    /// definition SQL Server stored — so a later pass can tell "unchanged" from "edited by hand".</summary>
    Task MarkDeployedAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string schema, string procedureName, string owner, string specHash, CancellationToken ct = default);

    string ComputeHash(string content);
    (string schema, string name)? ParseProcedureName(string sql);
}
