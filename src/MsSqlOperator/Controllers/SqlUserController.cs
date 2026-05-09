using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlUser), Verbs = RbacVerb.All)]
public class SqlUserController(
    ILogger<SqlUserController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter) : IEntityController<SqlUser>
{
    public async Task<ReconciliationResult<SqlUser>> ReconcileAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlUser {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var login = entity.Spec.Login;
        if (login == null)
        {
            logger.LogWarning("SqlUser {Name} has no login spec", entity.Metadata.Name);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        // Resolve connection target
        var (host, port, password) = await ResolveTargetAsync(entity, ns, ct);
        if (host == null || password == null)
        {
            logger.LogWarning("Cannot resolve target instance for SqlUser {Name}", entity.Metadata.Name);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        // Resolve login password
        var loginPassword = await ResolveLoginPasswordAsync(login, ns, ct);
        if (loginPassword == null)
        {
            logger.LogWarning("Login password secret not found for SqlUser {Name}", entity.Metadata.Name);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        // Create or update login
        if (!await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
        {
            await sqlUserManager.CreateLoginAsync(connection, login.Name, loginPassword, login.DefaultDatabase, login.CheckPolicy, login.CheckExpiration, ct: ct);
            logger.LogInformation("Created login {Login}", login.Name);
        }

        // Server permissions
        if (entity.Spec.ServerPermissions != null)
        {
            foreach (var perm in entity.Spec.ServerPermissions)
            {
                await sqlUserManager.GrantServerPermissionAsync(connection, login.Name, perm, ct);
            }
        }

        // Database permissions
        var dbUserStatuses = new List<DatabaseUserStatus>();
        if (entity.Spec.DatabasePermissions != null)
        {
            var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);

            foreach (var dbPerm in entity.Spec.DatabasePermissions)
            {
                var targetDbs = databaseFilter.Apply(allDatabases, dbPerm.Databases, dbPerm.DatabaseFilter);

                foreach (var db in targetDbs)
                {
                    if (!await sqlUserManager.DatabaseUserExistsAsync(connection, db, login.Name, ct))
                    {
                        await sqlUserManager.CreateDatabaseUserAsync(connection, db, login.Name, login.Name, ct);
                    }

                    var assignedRoles = new List<string>();
                    if (dbPerm.Roles != null)
                    {
                        foreach (var role in dbPerm.Roles)
                        {
                            await sqlUserManager.AddRoleMemberAsync(connection, db, login.Name, role, ct);
                            assignedRoles.Add(role);
                        }
                    }

                    if (dbPerm.SchemaPermissions != null)
                    {
                        foreach (var sp in dbPerm.SchemaPermissions)
                        {
                            foreach (var perm in sp.Permissions)
                            {
                                await sqlUserManager.GrantSchemaPermissionAsync(connection, db, login.Name, sp.Schema, perm, ct);
                            }
                        }
                    }

                    dbUserStatuses.Add(new DatabaseUserStatus { Database = db, Created = true, Roles = assignedRoles });
                }
            }
        }

        // Update status
        entity.Status ??= new SqlUserStatus();
        entity.Status.LoginCreated = true;
        entity.Status.DatabaseUsers = dbUserStatuses;
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready", Status = "True", Reason = "UserConfigured",
                Message = $"Login created, {dbUserStatuses.Count} database users configured",
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        logger.LogInformation("SqlUser {Name} reconciled: {DbCount} databases", entity.Metadata.Name, dbUserStatuses.Count);
        return ReconciliationResult<SqlUser>.Success(entity);
    }

    public Task<ReconciliationResult<SqlUser>> DeletedAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("SqlUser {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlUser>.Success(entity));
    }

    private async Task<(string? host, int port, string? password)> ResolveTargetAsync(SqlUser entity, string ns, CancellationToken ct)
    {
        var instanceRef = entity.Spec.InstanceRef;
        if (instanceRef != null)
        {
            var instance = await kubernetesClient.GetAsync<SqlInstance>(instanceRef.Name, ns, ct);
            if (instance?.Spec.SaPasswordSecretRef != null)
            {
                var pwd = await ResolveSecretValueAsync(instance.Spec.SaPasswordSecretRef, ns, ct);
                var host = $"{instanceRef.Name}.{ns}.svc.cluster.local";
                return (host, instance.Spec.Service?.Port ?? 1433, pwd);
            }
        }
        return (null, 1433, null);
    }

    private async Task<string?> ResolveLoginPasswordAsync(LoginSpec login, string ns, CancellationToken ct)
    {
        if (login.PasswordSecretRef == null) return null;
        return await ResolveSecretValueAsync(login.PasswordSecretRef, ns, ct);
    }

    private async Task<string?> ResolveSecretValueAsync(SecretReference secretRef, string ns, CancellationToken ct)
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
