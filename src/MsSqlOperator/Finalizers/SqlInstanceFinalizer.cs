using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlInstanceFinalizer(
    ILogger<SqlInstanceFinalizer> logger,
    IKubernetesClient kubernetesClient) : IEntityFinalizer<SqlInstance>
{
    public Task<ReconciliationResult<SqlInstance>> FinalizeAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlInstance {Name}", entity.Metadata.Name);
        // StatefulSet, Services, PVCs are cleaned up via owner references
        logger.LogInformation("SqlInstance {Name} finalized", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }
}
