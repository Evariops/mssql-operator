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
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlUser), Verbs = RbacVerb.All)]
public class SqlUserController(
    ILogger<SqlUserController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher) : IEntityController<SqlUser>
{
    public async Task<ReconciliationResult<SqlUser>> ReconcileAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlUser {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var login = entity.Spec.Login;
        if (login == null)
        {
            logger.LogWarning("SqlUser {Name} has no login", entity.Metadata.Name);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        entity.Status ??= new SqlUserStatus();

        // ── Compute effective expiration ──────────────────────
        var requeueAfter = ComputeExpiration(entity);

        // ── Check if expired ──────────────────────────────────
        if (entity.Status.Expired)
        {
            var hasPending = await HandleRevocationAsync(entity, ns, login, ct);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

            if (hasPending)
                return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromSeconds(30));

            return ReconciliationResult<SqlUser>.Success(entity);
        }

        // ── Check for reactivation ────────────────────────────
        if (entity.Status.RevokedAt != null && !entity.Status.Expired)
        {
            await HandleReactivationAsync(entity, ns, login, ct);
        }

        // ── Resolve login password ────────────────────────────
        var loginPassword = await ResolveLoginPasswordAsync(login, ns, ct);
        if (loginPassword == null)
        {
            logger.LogWarning("Login password secret not found for SqlUser {Name}", entity.Metadata.Name);
            SetCondition(entity, "Ready", "False", "PasswordSecretMissing", "Login password secret not found");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // ── Resolve targets via selector ──────────────────────
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        entity.Status.TargetStatuses ??= [];

        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            var overrides = FindOverride(entity, target);
            try
            {
                await ProvisionTargetAsync(entity, target, targetStatus, overrides, login, loginPassword, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot provision SqlUser {Name} on target {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "TargetUnreachable", $"Cannot connect to {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        // ── Update status ─────────────────────────────────────
        SetCondition(entity, "Ready", "True", "UserConfigured", "All targets provisioned");
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        var totalDbs = entity.Status.TargetStatuses?.Sum(t => t.DatabaseUsers?.Count ?? 0) ?? 0;
        await eventPublisher.PublishAsync(entity, "UserProvisioned", $"Login provisioned on {resolvedTargets.Count} target(s), {totalDbs} database users", ct: ct);

        if (requeueAfter.HasValue)
            return ReconciliationResult<SqlUser>.Success(entity, requeueAfter.Value);

        return ReconciliationResult<SqlUser>.Success(entity);
    }

    public Task<ReconciliationResult<SqlUser>> DeletedAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("SqlUser {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlUser>.Success(entity));
    }

    // ── Expiration ────────────────────────────────────────────

    private TimeSpan? ComputeExpiration(SqlUser entity)
    {
        var expiration = entity.Spec.Expiration;
        if (expiration == null) return null;

        DateTime? effectiveExpiresAt = null;

        if (!string.IsNullOrEmpty(expiration.ExpiresAt) && DateTime.TryParse(expiration.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            effectiveExpiresAt = parsed.ToUniversalTime();
        }
        else if (!string.IsNullOrEmpty(expiration.Ttl))
        {
            var (valid, duration, _) = TtlParser.Parse(expiration.Ttl);
            if (valid && entity.Metadata.CreationTimestamp.HasValue)
                effectiveExpiresAt = entity.Metadata.CreationTimestamp.Value.ToUniversalTime() + duration;
        }

        if (effectiveExpiresAt == null) return null;

        entity.Status.EffectiveExpiresAt = effectiveExpiresAt.Value.ToString("O");

        if (DateTime.UtcNow >= effectiveExpiresAt.Value)
        {
            entity.Status.Expired = true;
            return null;
        }

        // Reactivation: if the expiration was pushed into the future, clear expired/revoked state
        if (entity.Status.Expired)
        {
            entity.Status.Expired = false;
            entity.Status.RevokedAt = null;
        }

        return effectiveExpiresAt.Value - DateTime.UtcNow;
    }

    // ── Revocation ────────────────────────────────────────────

    private async Task<bool> HandleRevocationAsync(SqlUser entity, string ns, LoginSpec login, CancellationToken ct)
    {
        var action = entity.Spec.Expiration?.Action ?? "Disable";
        var terminateSessions = entity.Spec.Expiration?.TerminateSessions ?? true;
        var hasPending = false;

        entity.Status.RevokedAt ??= DateTime.UtcNow;
        entity.Status.TargetStatuses ??= [];

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            if (!targetStatus.RevocationPending && targetStatus.RevokedAt != null)
                continue;

            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                foreach (var (connection, replicaName) in connections)
                {
                    await using (connection)
                    {
                        if (terminateSessions)
                        {
                            var killed = await sqlUserManager.KillSessionsAsync(connection, login.Name, ct);
                            targetStatus.SessionsKilled += killed;
                        }

                        if (action.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                        {
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
                                }
                            }

                            if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                                await sqlUserManager.DropLoginAsync(connection, login.Name, ct);

                            targetStatus.LoginCreated = false;
                        }
                        else
                        {
                            if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                                await sqlUserManager.DisableLoginAsync(connection, login.Name, ct);

                            targetStatus.LoginDisabled = true;
                        }
                    }
                }

                targetStatus.RevokedAt = DateTime.UtcNow;
                targetStatus.RevocationPending = false;
                targetStatus.LastRevocationError = null;
                targetStatus.DatabaseUsers = [];
            }
            catch (Exception ex)
            {
                targetStatus.RevocationPending = true;
                targetStatus.LastRevocationError = ex.Message;
                hasPending = true;
                logger.LogWarning(ex, "Revocation failed for SqlUser {Name} on {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "TargetUnreachable", $"Revocation pending on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        var eventName = action.Equals("Delete", StringComparison.OrdinalIgnoreCase) ? "UserDeleted" : "UserExpired";
        await eventPublisher.PublishAsync(entity, eventName, $"Login {login.Name} {action.ToLowerInvariant()}d on expired SqlUser", "Warning", ct);

        SetCondition(entity, "Expired", "True", eventName, $"Expired at {entity.Status.EffectiveExpiresAt}");
        SetCondition(entity, "RevocationPending", hasPending ? "True" : "False", hasPending ? "PendingTargets" : "AllRevoked", null);

        return hasPending;
    }

    // ── Reactivation ──────────────────────────────────────────

    private async Task HandleReactivationAsync(SqlUser entity, string ns, LoginSpec login, CancellationToken ct)
    {
        logger.LogInformation("Reactivating SqlUser {Name}", entity.Metadata.Name);

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            if (!targetStatus.LoginDisabled && targetStatus.LoginCreated) continue;

            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                foreach (var (connection, _) in connections)
                {
                    await using (connection)
                    {
                        if (targetStatus.LoginDisabled && await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                        {
                            await sqlUserManager.EnableLoginAsync(connection, login.Name, ct);
                            targetStatus.LoginDisabled = false;
                        }
                    }
                }
                targetStatus.RevocationPending = false;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reactivation failed for SqlUser {Name} on {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
            }
        }

        SetCondition(entity, "Expired", "False", "Reactivated", "User reactivated with new expiration");
        SetCondition(entity, "RevocationPending", "False", "Reactivated", null);
        await eventPublisher.PublishAsync(entity, "UserReactivated", $"Login {login.Name} reactivated", ct: ct);
    }

    // ── Provisioning ──────────────────────────────────────────

    private async Task ProvisionTargetAsync(SqlUser entity, ResolvedTarget target, UserTargetStatus targetStatus,
        TargetOverride? overrides, LoginSpec login, string loginPassword, CancellationToken ct)
    {
        var connections = await ResolveTargetConnectionsAsync(target, ct);
        byte[]? sid = null;

        // Create login on each connection (instance or AG replicas)
        foreach (var (connection, replicaName) in connections)
        {
            await using (connection)
            {
                if (!await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                {
                    await sqlUserManager.CreateLoginAsync(connection, login.Name, loginPassword, login.DefaultDatabase, login.CheckPolicy, login.CheckExpiration, sid, ct);
                    logger.LogInformation("Created login {Login} on {Target}", login.Name, replicaName ?? $"{target.Kind}/{target.Name}");
                }
                else
                {
                    // Ensure password is in sync (handles secret rotation)
                    await sqlUserManager.AlterLoginPasswordAsync(connection, login.Name, loginPassword, ct);
                }

                // Capture SID from first replica for AG consistency
                if (sid == null && target.Kind == "SqlAvailabilityGroup")
                    sid = await sqlUserManager.GetLoginSidAsync(connection, login.Name, ct);

                // Server roles (override or global)
                var serverRoles = overrides?.ServerRoles ?? entity.Spec.ServerRoles;
                if (serverRoles != null)
                    foreach (var role in serverRoles)
                        await sqlUserManager.AddServerRoleMemberAsync(connection, login.Name, role, ct);

                // Server permissions (override or global)
                var serverPermissions = overrides?.ServerPermissions ?? entity.Spec.ServerPermissions;
                if (serverPermissions != null)
                    foreach (var perm in serverPermissions)
                        await sqlUserManager.GrantServerPermissionAsync(connection, login.Name, perm, ct);
            }
        }

        targetStatus.LoginCreated = true;

        // Database permissions — only on primary (first connection)
        var dbPermissions = overrides?.DatabasePermissions ?? entity.Spec.DatabasePermissions;
        if (dbPermissions != null && connections.Count > 0)
        {
            var resolvedConns = await ResolveTargetConnectionsAsync(target, ct);
            if (resolvedConns.Count > 0)
            {
                await using var dbConn = resolvedConns[0].Connection;
                var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(dbConn, ct);
                targetStatus.DatabaseUsers = [];

                foreach (var dbPerm in dbPermissions)
                {
                    var targetDbs = databaseFilter.Apply(allDatabases, dbPerm.Databases, dbPerm.DatabaseFilter);
                    foreach (var db in targetDbs)
                    {
                        if (!await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                            await sqlUserManager.CreateDatabaseUserAsync(dbConn, db, login.Name, login.Name, ct);

                        var assignedRoles = new List<string>();
                        if (dbPerm.Roles != null)
                        {
                            foreach (var role in dbPerm.Roles)
                            {
                                await sqlUserManager.AddRoleMemberAsync(dbConn, db, login.Name, role, ct);
                                assignedRoles.Add(role);
                            }
                        }

                        if (dbPerm.SchemaPermissions != null)
                        {
                            foreach (var sp in dbPerm.SchemaPermissions)
                                foreach (var perm in sp.Permissions)
                                    await sqlUserManager.GrantSchemaPermissionAsync(dbConn, db, login.Name, sp.Schema, perm, ct);
                        }

                        targetStatus.DatabaseUsers.Add(new DatabaseUserStatus { Database = db, Created = true, Roles = assignedRoles });
                    }
                }
            }
        }
    }

    // ── Connection resolution ─────────────────────────────────

    private async Task<List<(Microsoft.Data.SqlClient.SqlConnection Connection, string? ReplicaName)>> ResolveTargetConnectionsAsync(ResolvedTarget target, CancellationToken ct)
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

    // ── Helpers ───────────────────────────────────────────────

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

    private static TargetOverride? FindOverride(SqlUser entity, ResolvedTarget target)
    {
        if (entity.Spec.Overrides == null) return null;
        return target.Kind switch
        {
            "SqlInstance" => entity.Spec.Overrides.FirstOrDefault(o => o.Instance == target.Name),
            "SqlAvailabilityGroup" => entity.Spec.Overrides.FirstOrDefault(o => o.AvailabilityGroup == target.Name),
            _ => null
        };
    }

    private static UserTargetStatus GetOrCreateTargetStatus(SqlUser entity, ResolvedTarget target)
    {
        entity.Status.TargetStatuses ??= [];
        var existing = entity.Status.TargetStatuses.FirstOrDefault(ts =>
            ts.Kind == target.Kind && ts.Name == target.Name && ts.Namespace == target.Namespace);

        if (existing != null) return existing;

        var status = new UserTargetStatus
        {
            Kind = target.Kind,
            Name = target.Name,
            Namespace = target.Namespace
        };
        entity.Status.TargetStatuses.Add(status);
        return status;
    }

    private static void SetCondition(SqlUser entity, string type, string status, string reason, string? message)
    {
        entity.Status.Conditions ??= [];
        var existing = entity.Status.Conditions.FirstOrDefault(c => c.Type == type);
        if (existing != null)
        {
            existing.Status = status;
            existing.Reason = reason;
            existing.Message = message;
            existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            entity.Status.Conditions.Add(new ConditionStatus
            {
                Type = type, Status = status, Reason = reason,
                Message = message, LastTransitionTime = DateTime.UtcNow
            });
        }
    }
}
