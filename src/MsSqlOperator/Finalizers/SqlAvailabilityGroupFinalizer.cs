using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlAvailabilityGroupFinalizer(
    ILogger<SqlAvailabilityGroupFinalizer> logger,
    IKubernetesClient kubernetesClient) : IEntityFinalizer<SqlAvailabilityGroup>
{
    public Task<ReconciliationResult<SqlAvailabilityGroup>> FinalizeAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlAvailabilityGroup {Name}", entity.Metadata.Name);
        // AG and related resources cleaned up via owner references
        logger.LogInformation("SqlAvailabilityGroup {Name} finalized", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroup>.Success(entity));
    }
}
