using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlInstanceFinalizer(
    ILogger<SqlInstanceFinalizer> logger) : IEntityFinalizer<SqlInstance>
{
    public Task<ReconciliationResult<SqlInstance>> FinalizeAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlInstance {Name}", entity.Metadata.Name);
        // StatefulSet, Services, PVCs are cleaned up via owner references
        logger.LogInformation("SqlInstance {Name} finalized", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }
}
