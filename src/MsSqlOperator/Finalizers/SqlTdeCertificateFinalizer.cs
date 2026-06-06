using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlTdeCertificateFinalizer(
    ILogger<SqlTdeCertificateFinalizer> logger,
    IKubernetesClient kubernetesClient) : IEntityFinalizer<SqlTdeCertificate>
{
    public Task<ReconciliationResult<SqlTdeCertificate>> FinalizeAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlTdeCertificate {Name} — certificate backup should be preserved", entity.Metadata.Name);
        // Certificates should NOT be dropped on CR deletion — only backup and cleanup tracking
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }
}
