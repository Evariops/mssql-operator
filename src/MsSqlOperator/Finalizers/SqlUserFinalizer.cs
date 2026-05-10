using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlUserFinalizer(
    ILogger<SqlUserFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager,
    ITargetResolver targetResolver) : IEntityFinalizer<SqlUser>
{
    private const int MaxAttempts = 3;

    public async Task<ReconciliationResult<SqlUser>> FinalizeAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlUser {Name}", entity.Metadata.Name);

        var login = entity.Spec.Login;
        if (login == null) return ReconciliationResult<SqlUser>.Success(entity);

        var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql.operator/force-finalize", out var forceVal) == true
                            && forceVal == "true";

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        var failedTargets = new List<string>();

        foreach (var target in resolvedTargets)
        {
            var targetName = $"{target.Kind}/{target.Name}";
            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                foreach (var (connection, replicaName) in connections)
                {
                    await using (connection)
                    {
                        await sqlUserManager.KillSessionsAsync(connection, login.Name, ct);

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
                    entity.Metadata.Annotations ??= new Dictionary<string, string>();
                    var attemptKey = $"mssql.operator/finalize-attempts-{target.Name}";
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

    private async Task<List<(Microsoft.Data.SqlClient.SqlConnection Connection, string? ReplicaName)>> ResolveTargetConnectionsAsync(Entities.Common.ResolvedTarget target, CancellationToken ct)
    {
        var results = new List<(Microsoft.Data.SqlClient.SqlConnection, string?)>();

        if (target.Kind == "SqlAvailabilityGroup")
        {
            var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct);
            if (ag?.Spec != null)
            {
                for (var i = 0; i < ag.Spec.Replicas.Count; i++)
                {
                    var replicaName = $"{target.Name}-{i}";
                    var host = $"{replicaName}.{target.Namespace}.svc.cluster.local";
                    var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);
                    results.Add((conn, replicaName));
                }
            }
        }
        else
        {
            var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
            results.Add((conn, null));
        }

        return results;
    }
}
