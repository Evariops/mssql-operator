using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlTdeCertificateFinalizer(
    ILogger<SqlTdeCertificateFinalizer> logger) : IEntityFinalizer<SqlTdeCertificate>
{
    public string Identifier => "mssql-operator.evariops.com/sqltdecertificate-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqltdecertificatefinalizer.finalizers.mssql-operator.evariops.com"];

    public Task<ReconciliationResult<SqlTdeCertificate>> FinalizeAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlTdeCertificate {Name} — certificate backup should be preserved", entity.Metadata.Name);
        // Certificates should NOT be dropped on CR deletion — only backup and cleanup tracking
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }
}
