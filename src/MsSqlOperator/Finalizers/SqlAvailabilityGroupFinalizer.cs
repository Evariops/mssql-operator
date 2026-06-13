using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlAvailabilityGroupFinalizer(
    ILogger<SqlAvailabilityGroupFinalizer> logger,
    IKubernetesClient kubernetesClient,
    IEventPublisher eventPublisher) : IEntityFinalizer<SqlAvailabilityGroup>
{
    public async Task<ReconciliationResult<SqlAvailabilityGroup>> FinalizeAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty;

        logger.LogInformation("Finalizing SqlAvailabilityGroup {Name}", name);

        // Check for force-cleanup annotation
        var forceCleanup = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/finalizer-policy", out var policy) == true
                           && policy == "force-kubernetes-cleanup";

        if (!forceCleanup)
        {
            // Check if any SqlAvailabilityGroupDatabase CRDs still reference this AG
            List<string> blocking;
            try
            {
                var sagdbs = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct);
                blocking = sagdbs
                    .Where(s => s.Spec.AvailabilityGroupRef.Name == name)
                    .Select(s => s.Metadata.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check for SqlAvailabilityGroupDatabase CRDs referencing AG {Ag}", name);
                blocking = [];
            }

            if (blocking.Count > 0)
            {
                logger.LogWarning("Cannot delete AG {Ag}: {Count} SqlAvailabilityGroupDatabase CRDs still reference it: {Names}",
                    name, blocking.Count, string.Join(", ", blocking));
                await eventPublisher.PublishAsync(entity, "AgDeletionBlockedByDatabases",
                    $"Cannot delete AG: {blocking.Count} SqlAvailabilityGroupDatabase CRDs still reference it ({string.Join(", ", blocking.Take(5))}). Delete them first or use annotation mssql-operator.evariops.com/finalizer-policy: force-kubernetes-cleanup",
                    "Warning", ct);
                // Block finalization — throw to keep the finalizer and requeue
                throw new InvalidOperationException(
                    $"AG '{name}' cannot be deleted: {blocking.Count} SqlAvailabilityGroupDatabase CRDs still reference it");
            }
        }
        else
        {
            await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                $"Force cleanup requested for AG {name}. SQL artifacts may remain orphaned.", "Warning", ct);
        }

        // AG and related resources cleaned up via owner references
        logger.LogInformation("SqlAvailabilityGroup {Name} finalized", name);
        return ReconciliationResult<SqlAvailabilityGroup>.Success(entity);
    }
}
