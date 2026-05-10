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
    private const int MaxAttempts = 3;

    public async Task<ReconciliationResult<SqlUser>> FinalizeAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlUser {Name}", entity.Metadata.Name);

        var login = entity.Spec.Login;
        if (login == null) return ReconciliationResult<SqlUser>.Success(entity);

        if (entity.Spec.Targets == null || entity.Spec.Targets.Count == 0)
            return ReconciliationResult<SqlUser>.Success(entity);

        var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql.operator/force-finalize", out var forceVal) == true
                            && forceVal == "true";

        var ns = entity.Metadata.NamespaceProperty;
        var failedTargets = new List<string>();

        foreach (var target in entity.Spec.Targets)
        {
            var targetName = target.InstanceRef?.Name ?? target.AvailabilityGroupRef?.Name ?? "unknown";
            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ns, ct);
                foreach (var (connection, replicaName) in connections)
                {
                    await using (connection)
                    {
                        // Kill active sessions
                        await sqlUserManager.KillSessionsAsync(connection, login.Name, ct);

                        // Drop database users with schema ownership transfer
                        var databases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);
                        foreach (var db in databases)
                        {
                            if (await sqlUserManager.DatabaseUserExistsAsync(connection, db, login.Name, ct))
                            {
                                try
                                {
                                    await sqlUserManager.DropDatabaseUserAsync(connection, db, login.Name, ct);
                                }
                                catch
                                {
                                    await sqlUserManager.TransferSchemaOwnershipAsync(connection, db, login.Name, ct);
                                    await sqlUserManager.DropDatabaseUserAsync(connection, db, login.Name, ct);
                                }
                                logger.LogInformation("Dropped user {User} from {Db} on {Target}", login.Name, db, replicaName ?? targetName);
                            }
                        }

                        // Drop login
                        if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                        {
                            await sqlUserManager.DropLoginAsync(connection, login.Name, ct);
                            logger.LogInformation("Dropped login {Login} on {Target}", login.Name, replicaName ?? targetName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failedTargets.Add(targetName);
                logger.LogWarning(ex, "Error during SqlUser finalization for {Name} on {Target}", entity.Metadata.Name, targetName);

                if (!forceFinalize)
                {
                    // Track attempts via annotation
                    entity.Metadata.Annotations ??= new Dictionary<string, string>();
                    var attemptKey = $"mssql.operator/finalize-attempts-{targetName}";
                    var attempts = 1;
                    if (entity.Metadata.Annotations.TryGetValue(attemptKey, out var attemptsStr))
                        attempts = int.Parse(attemptsStr) + 1;
                    entity.Metadata.Annotations[attemptKey] = attempts.ToString();

                    if (attempts < MaxAttempts)
                    {
                        logger.LogWarning("Finalization attempt {Attempt}/{Max} for {Name} on {Target}, will retry", attempts, MaxAttempts, entity.Metadata.Name, targetName);
                        return ReconciliationResult<SqlUser>.Failure(entity, $"Finalization failed on {targetName}, attempt {attempts}/{MaxAttempts}");
                    }

                    logger.LogError("Abandoning cleanup after {Max} attempts for {Name} on {Target}", MaxAttempts, entity.Metadata.Name, targetName);
                }
            }
        }

        return ReconciliationResult<SqlUser>.Success(entity);
    }

    private async Task<List<(Microsoft.Data.SqlClient.SqlConnection Connection, string? ReplicaName)>> ResolveTargetConnectionsAsync(UserTarget target, string ns, CancellationToken ct)
    {
        var results = new List<(Microsoft.Data.SqlClient.SqlConnection, string?)>();

        if (target.InstanceRef != null)
        {
            var instance = await kubernetesClient.GetAsync<SqlInstance>(target.InstanceRef.Name, ns, ct);
            if (instance?.Spec.SaPasswordSecretRef != null)
            {
                var pwd = await ResolveSecretValueAsync(instance.Spec.SaPasswordSecretRef, ns, ct);
                if (pwd != null)
                {
                    var host = $"{target.InstanceRef.Name}.{ns}.svc.cluster.local";
                    var conn = await connectionFactory.CreateConnectionAsync(host, instance.Spec.Service?.Port ?? 1433, pwd, "master", ct);
                    results.Add((conn, null));
                }
            }
        }
        else if (target.AvailabilityGroupRef != null)
        {
            var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.AvailabilityGroupRef.Name, ns, ct);
            if (ag?.Spec?.SaPasswordSecretRef != null)
            {
                var pwd = await ResolveSecretValueAsync(ag.Spec.SaPasswordSecretRef, ns, ct);
                if (pwd != null)
                {
                    for (var i = 0; i < ag.Spec.Replicas.Count; i++)
                    {
                        var replicaName = $"{target.AvailabilityGroupRef.Name}-{i}";
                        var host = $"{replicaName}.{ns}.svc.cluster.local";
                        var conn = await connectionFactory.CreateConnectionAsync(host, 1433, pwd, "master", ct);
                        results.Add((conn, replicaName));
                    }
                }
            }
        }

        return results;
    }

    private async Task<string?> ResolveSecretValueAsync(Entities.Common.SecretReference secretRef, string ns, CancellationToken ct)
    {
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(secretRef.Key, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }
}
