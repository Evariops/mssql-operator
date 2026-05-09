using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

public class SqlUserFinalizer(
    ILogger<SqlUserFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager) : IEntityFinalizer<SqlUser>
{
    public async Task<ReconciliationResult<SqlUser>> FinalizeAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlUser {Name}", entity.Metadata.Name);

        var login = entity.Spec.Login;
        if (login == null) return ReconciliationResult<SqlUser>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        if (entity.Spec.InstanceRef == null) return ReconciliationResult<SqlUser>.Success(entity);

        try
        {
            var instance = await kubernetesClient.GetAsync<SqlInstance>(entity.Spec.InstanceRef.Name, ns, ct);
            if (instance?.Spec.SaPasswordSecretRef == null) return ReconciliationResult<SqlUser>.Success(entity);

            var secret = await kubernetesClient.GetAsync<V1Secret>(instance.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return ReconciliationResult<SqlUser>.Success(entity);

            if (!secret.Data.TryGetValue(instance.Spec.SaPasswordSecretRef.Key, out var pwdBytes)) return ReconciliationResult<SqlUser>.Success(entity);
            var password = System.Text.Encoding.UTF8.GetString(pwdBytes);
            var host = $"{entity.Spec.InstanceRef.Name}.{ns}.svc.cluster.local";

            await using var connection = await connectionFactory.CreateConnectionAsync(host, instance.Spec.Service?.Port ?? 1433, password, "master", ct);

            // Drop database users first
            var databases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);
            foreach (var db in databases)
            {
                if (await sqlUserManager.DatabaseUserExistsAsync(connection, db, login.Name, ct))
                {
                    await sqlUserManager.DropDatabaseUserAsync(connection, db, login.Name, ct);
                    logger.LogInformation("Dropped user {User} from database {Db}", login.Name, db);
                }
            }

            // Drop login
            if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
            {
                await sqlUserManager.DropLoginAsync(connection, login.Name, ct);
                logger.LogInformation("Dropped login {Login}", login.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error during SqlUser finalization for {Name}", entity.Metadata.Name);
        }

        return ReconciliationResult<SqlUser>.Success(entity);
    }
}
