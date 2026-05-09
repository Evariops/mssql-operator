using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

public class SqlStoredProcedureFinalizer(
    ILogger<SqlStoredProcedureFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlStoredProcedureManager storedProcManager) : IEntityFinalizer<SqlStoredProcedure>
{
    public async Task<ReconciliationResult<SqlStoredProcedure>> FinalizeAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlStoredProcedure {Name}", entity.Metadata.Name);

        if (entity.Status?.DeployedProcedures == null) return ReconciliationResult<SqlStoredProcedure>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        if (entity.Spec.InstanceRef == null) return ReconciliationResult<SqlStoredProcedure>.Success(entity);

        try
        {
            var instance = await kubernetesClient.GetAsync<SqlInstance>(entity.Spec.InstanceRef.Name, ns, ct);
            if (instance?.Spec.SaPasswordSecretRef == null) return ReconciliationResult<SqlStoredProcedure>.Success(entity);

            var secret = await kubernetesClient.GetAsync<V1Secret>(instance.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return ReconciliationResult<SqlStoredProcedure>.Success(entity);

            if (!secret.Data.TryGetValue(instance.Spec.SaPasswordSecretRef.Key, out var pwdBytes)) return ReconciliationResult<SqlStoredProcedure>.Success(entity);
            var password = System.Text.Encoding.UTF8.GetString(pwdBytes);
            var host = $"{entity.Spec.InstanceRef.Name}.{ns}.svc.cluster.local";

            await using var connection = await connectionFactory.CreateConnectionAsync(host, instance.Spec.Service?.Port ?? 1433, password, "master", ct);

            foreach (var dbProcs in entity.Status.DeployedProcedures)
            {
                if (dbProcs.Procedures == null) continue;
                foreach (var proc in dbProcs.Procedures)
                {
                    await storedProcManager.DropProcedureAsync(connection, dbProcs.Database, proc.Schema, proc.Name, ct);
                    logger.LogInformation("Dropped procedure [{Schema}].[{Name}] from {Db}", proc.Schema, proc.Name, dbProcs.Database);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error during SqlStoredProcedure finalization for {Name}", entity.Metadata.Name);
        }

        return ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }
}
