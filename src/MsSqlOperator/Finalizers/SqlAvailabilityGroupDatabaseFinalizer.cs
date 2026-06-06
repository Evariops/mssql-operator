using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

public class SqlAvailabilityGroupDatabaseFinalizer(
    ILogger<SqlAvailabilityGroupDatabaseFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlAgManager agManager,
    IEventPublisher eventPublisher) : IEntityFinalizer<SqlAvailabilityGroupDatabase>
{
    public async Task<ReconciliationResult<SqlAvailabilityGroupDatabase>> FinalizeAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        logger.LogInformation("Finalizing SqlAvailabilityGroupDatabase {Name} (db={Db}, ag={Ag})",
            entity.Metadata.Name, dbName, agName);

        // Check for force-cleanup annotation
        var forceCleanup = entity.Metadata.Annotations?.TryGetValue("mssql.operator/finalizer-policy", out var policy) == true
                           && policy == "force-kubernetes-cleanup";

        if (forceCleanup)
        {
            logger.LogWarning("Force cleanup requested for {Name}, skipping SQL cleanup", entity.Metadata.Name);
            await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                $"Database '{dbName}' may still be in AG '{agName}' (force cleanup)", "Warning", ct);
            return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
        }

        // Set phase to Removing
        entity = await UpdateStatus(entity, "Removing", "DeletionBlocked", "False",
            "Removing database from AG", ct);

        // Resolve the AG to find the primary
        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
        if (ag?.Status?.PrimaryReplica == null)
        {
            logger.LogWarning("Cannot finalize {Name}: AG {Ag} primary not available — keeping finalizer",
                entity.Metadata.Name, agName);
            await eventPublisher.PublishAsync(entity, "DeletionBlocked",
                $"Cannot remove database from AG: primary is not reachable. Finalizer kept.", "Warning", ct);
            entity = await UpdateStatus(entity, "Removing", "DeletionBlocked", "True",
                "Primary is not reachable. Use annotation mssql.operator/finalizer-policy: force-kubernetes-cleanup to bypass.", ct);
            // Keep finalizer — requeue. Throwing makes KubeOps requeue the entity.
            throw new InvalidOperationException($"AG '{agName}' primary not available, blocking finalization");
        }

        var saPassword = await ResolveSaPasswordFromAgAsync(ag, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("Cannot resolve SA password for AG {Ag} — keeping finalizer", agName);
            entity = await UpdateStatus(entity, "Removing", "DeletionBlocked", "True",
                "Cannot resolve SA password from AG", ct);
            throw new InvalidOperationException($"SA password for AG '{agName}' not resolvable, blocking finalization");
        }

        var headlessService = $"{agName}-headless";
        var primaryFqdn = $"{ag.Status.PrimaryReplica}.{headlessService}.{ns}.svc.cluster.local";

        try
        {
            await using var conn = await connectionFactory.CreateConnectionAsync(primaryFqdn, 1433, saPassword, "master", ct);

            // Check if DB is actually in the AG before trying to remove
            var existingAg = await agManager.GetDatabaseAgNameAsync(conn, dbName, ct);
            if (existingAg != null && existingAg.Equals(agName, StringComparison.OrdinalIgnoreCase))
            {
                await agManager.RemoveDatabaseFromAgAsync(conn, agName, dbName, ct);
                logger.LogInformation("Removed database {Db} from AG {Ag}", dbName, agName);
                await eventPublisher.PublishAsync(entity, "DatabaseRemovedFromAg",
                    $"Database '{dbName}' removed from AG '{agName}'. Database NOT dropped.", ct: ct);
            }
            else
            {
                logger.LogInformation("Database {Db} not in AG {Ag}, skipping removal", dbName, agName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove database {Db} from AG {Ag} during finalization — keeping finalizer",
                dbName, agName);
            await eventPublisher.PublishAsync(entity, "DatabaseRemovalFailed",
                $"Failed to remove database '{dbName}' from AG: {ex.Message}. Finalizer kept.", "Warning", ct);
            entity = await UpdateStatus(entity, "Removing", "DeletionBlocked", "True",
                $"SQL error during removal: {ex.Message}", ct);
            // Block finalization on SQL errors — keep finalizer
            throw;
        }

        logger.LogInformation("SqlAvailabilityGroupDatabase {Name} finalized", entity.Metadata.Name);
        return ReconciliationResult<SqlAvailabilityGroupDatabase>.Success(entity);
    }

    private async Task<SqlAvailabilityGroupDatabase> UpdateStatus(
        SqlAvailabilityGroupDatabase entity, string phase, string conditionType, string conditionStatus,
        string message, CancellationToken ct)
    {
        entity.Status ??= new SqlAvailabilityGroupDatabaseStatus();
        entity.Status.Phase = phase;

        // Preserve existing conditions but upsert the target condition
        var conditions = entity.Status.Conditions ?? [];
        var existing = conditions.FirstOrDefault(c => c.Type == conditionType);
        if (existing != null)
        {
            existing.Status = conditionStatus;
            existing.Message = message;
            existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            conditions.Add(new ConditionStatus
            {
                Type = conditionType,
                Status = conditionStatus,
                Reason = conditionType,
                Message = message,
                LastTransitionTime = DateTime.UtcNow
            });
        }
        entity.Status.Conditions = conditions;

        try { return await kubernetesClient.UpdateStatusAsync(entity, ct); }
        catch { return entity; }
    }

    private async Task<string?> ResolveSaPasswordFromAgAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        var secretRef = ag.Spec.SaPasswordSecretRef;
        if (secretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(secretRef.Key, out var bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }
}
