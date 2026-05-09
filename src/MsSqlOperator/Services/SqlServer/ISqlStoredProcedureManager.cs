namespace MsSqlOperator.Services.SqlServer;

public interface ISqlStoredProcedureManager
{
    Task<bool> ProcedureExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default);
    Task DeployProcedureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string sql, CancellationToken ct = default);
    Task DropProcedureAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string schema, string procedureName, CancellationToken ct = default);
    Task<bool> ValidateSyntaxAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string sql, CancellationToken ct = default);
    Task DeployInTransactionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, List<string> sqlStatements, CancellationToken ct = default);
    string ComputeHash(string content);
    (string schema, string name)? ParseProcedureName(string sql);
}
