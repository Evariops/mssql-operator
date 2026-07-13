using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
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
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher) : IEntityFinalizer<SqlUser>
{
    public string Identifier => "mssql-operator.evariops.com/sqluser-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqluserfinalizer.finalizers.mssql-operator.evariops.com"];

    private const int MaxAttempts = 3;

    public async Task<ReconciliationResult<SqlUser>> FinalizeAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlUser {Name}", entity.Metadata.Name);

        var login = entity.Spec.Login;
        if (login == null) return ReconciliationResult<SqlUser>.Success(entity);

        var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/force-finalize", out var forceVal) == true
                            && forceVal == "true";

        // Never DROP a login the operator did not create. status.appliedLoginName is only
        // stamped once this CR provisioned the login; without it, the login in SQL Server merely shares
        // the name — a DBA's 'reporting' login, say — and dropping it (plus its database users, with a
        // forced schema transfer to dbo) is an immediate production outage. This is the exact failure of
        // a CR that never converged (bad secretRef) and was deleted to be re-created.
        if (!string.Equals(entity.Status?.AppliedLoginName, login.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "SqlUser {Name}: login '{Login}' was never provisioned by this resource (status.appliedLoginName is not set); " +
                "leaving it untouched", entity.Metadata.Name, login.Name);
            await eventPublisher.PublishAsync(entity, "CleanupSkippedNotOwned",
                $"SQL cleanup skipped: login '{login.Name}' was not provisioned by this SqlUser, so it is not the operator's to drop.",
                ct: ct);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        var failedTargets = new List<string>();

        foreach (var target in resolvedTargets)
        {
            var targetName = $"{target.Kind}/{target.Name}";
            try
            {
                // Database users are dropped through the PRIMARY only — replication
                // propagates the drop, and DDL against read-only secondaries fails forever.
                // GetDatabaseNamesAsync already filters to writable databases.
                await using (var primary = await OpenPrimaryConnectionAsync(target, ct))
                {
                    if (primary == null)
                        throw new InvalidOperationException($"No primary replica reachable on {targetName}");

                    await sqlUserManager.KillSessionsAsync(primary, login.Name, ct);

                    var databases = await sqlServerManager.GetDatabaseNamesAsync(primary, ct);
                    foreach (var db in databases)
                    {
                        if (await sqlUserManager.DatabaseUserExistsAsync(primary, db, login.Name, ct))
                        {
                            try
                            {
                                await sqlUserManager.DropDatabaseUserAsync(primary, db, login.Name, ct);
                            }
                            catch
                            {
                                await sqlUserManager.TransferSchemaOwnershipAsync(primary, db, login.Name, ct);
                                await sqlUserManager.DropDatabaseUserAsync(primary, db, login.Name, ct);
                            }
                            logger.LogInformation("Dropped user {User} from {Db} on {Target}", login.Name, db, targetName);
                        }
                    }
                }

                // Logins are server-scoped (not replicated): drop on every replica.
                // The connections are opened eagerly, so a SQL error in the middle of the
                // loop used to leak every connection not yet reached by the `await using`. Dispose them
                // all in a finally, whatever happens.
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                try
                {
                    foreach (var (connection, replicaName) in connections)
                    {
                        if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                        {
                            await sqlUserManager.KillSessionsAsync(connection, login.Name, ct);
                            await sqlUserManager.DropLoginAsync(connection, login.Name, ct);
                            logger.LogInformation("Dropped login {Login} on {Target}", login.Name, replicaName ?? targetName);
                        }
                    }
                }
                finally
                {
                    foreach (var (connection, _) in connections)
                        await connection.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                failedTargets.Add(targetName);
                logger.LogWarning(ex, "Error during SqlUser finalization for {Name} on {Target}", entity.Metadata.Name, targetName);

                if (!forceFinalize)
                {
                    // Persist the attempt counter — the runtime re-fetches the object
                    // on every pass, so an in-memory increment was lost and MaxAttempts was dead.
                    entity.Metadata.Annotations ??= new Dictionary<string, string>();
                    var attemptKey = $"mssql-operator.evariops.com/finalize-attempts-{target.Name}";
                    var attempts = 1;
                    if (entity.Metadata.Annotations.TryGetValue(attemptKey, out var attemptsStr)
                        && int.TryParse(attemptsStr, out var prev))
                        attempts = prev + 1;
                    entity.Metadata.Annotations[attemptKey] = attempts.ToString();
                    try
                    {
                        var updated = await kubernetesClient.UpdateAsync(entity, ct);
                        entity.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
                    }
                    catch (Exception persistEx)
                    {
                        logger.LogWarning(persistEx, "Could not persist finalize-attempts for {Name}", entity.Metadata.Name);
                    }

                    if (attempts < MaxAttempts)
                    {
                        logger.LogWarning("Finalization attempt {Attempt}/{Max} for {Name} on {Target}, will retry", attempts, MaxAttempts, entity.Metadata.Name, targetName);
                        return ReconciliationResult<SqlUser>.Failure(entity, $"Finalization failed on {targetName}, attempt {attempts}/{MaxAttempts}");
                    }

                    logger.LogError("Abandoning cleanup after {Max} attempts for {Name} on {Target}", MaxAttempts, entity.Metadata.Name, targetName);
                    await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                        $"Abandoned SQL cleanup on {targetName} after {MaxAttempts} attempts; login '{login.Name}' may remain. " +
                        "Set annotation mssql-operator.evariops.com/force-finalize=true to skip cleanup explicitly.", "Warning", ct);
                }
            }
        }

        return ReconciliationResult<SqlUser>.Success(entity);
    }

    private async Task<Microsoft.Data.SqlClient.SqlConnection?> OpenPrimaryConnectionAsync(Entities.Common.ResolvedTarget target, CancellationToken ct)
    {
        if (target.Kind == "SqlAvailabilityGroup")
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(
                target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
            return primary?.Connection;
        }

        return await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
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
                    var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);
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
