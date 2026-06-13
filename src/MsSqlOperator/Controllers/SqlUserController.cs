using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

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
    IEventPublisher eventPublisher,
    OperatorMetrics metrics) : IEntityController<SqlUser>
{
    public async Task<ReconciliationResult<SqlUser>> ReconcileAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlUser {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var login = entity.Spec.Login;
        if (login == null)
        {
            logger.LogWarning("SqlUser {Name} has no login", entity.Metadata.Name);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        entity.Status ??= new SqlUserStatus();
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;

        // ── Compute effective expiration ──────────────────────
        var requeueAfter = ComputeExpiration(entity);

        // ── Check if expired ──────────────────────────────────
        if (entity.Status.Expired)
        {
            var hasPending = await HandleRevocationAsync(entity, ns, name, login, ct);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

            // Keep requeuing while any target could not be revoked yet (RevocationPending condition
            // is the cross-reconcile source of truth — there is no per-target status array anymore).
            if (hasPending)
                return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromSeconds(30));

            return ReconciliationResult<SqlUser>.Success(entity);
        }

        // ── Check for reactivation ────────────────────────────
        // ComputeExpiration already cleared Expired when the deadline was pushed to the future;
        // a lingering RevokedAt means we still have a disabled/dropped login to re-enable.
        if (entity.Status.RevokedAt != null)
        {
            await HandleReactivationAsync(entity, ns, login, ct);
        }

        // ── Resolve login password ────────────────────────────
        var loginPassword = await ResolveLoginPasswordAsync(login, ns, ct);
        if (loginPassword == null)
        {
            logger.LogWarning("Login password secret not found for SqlUser {Name}", entity.Metadata.Name);
            entity.Status.Phase = "Pending";
            SetCondition(entity, "Ready", "False", "PasswordSecretMissing", "Login password secret not found");
            metrics.SetTargets("SqlUser", ns, name, 0, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // ── Resolve targets via selector ──────────────────────
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        var provisioned = 0;
        var failures = new List<string>();
        foreach (var target in resolvedTargets)
        {
            var overrides = FindOverride(entity, target);
            try
            {
                await ProvisionTargetAsync(entity, target, overrides, login, loginPassword, ct);
                provisioned++;
                await eventPublisher.PublishAsync(entity, "TargetProvisioned",
                    $"Login {login.Name} provisioned on {target.Kind}/{target.Name}", ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot provision SqlUser {Name} on target {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
                failures.Add($"{target.Kind}/{target.Name}: {ex.Message}");
                await eventPublisher.PublishAsync(entity, "ProvisionFailed",
                    $"Cannot provision login on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        // ── Update status ─────────────────────────────────────
        // The condition must reflect the real provisioning outcome: a caught failure is
        // not success. Reporting Ready=True on a failed CREATE LOGIN masks the problem.
        entity.Status.TotalTargets = resolvedTargets.Count;
        entity.Status.ReadyTargets = provisioned;
        entity.Status.FailedTargets = failures.Count;

        if (resolvedTargets.Count == 0)
        {
            entity.Status.Phase = "Pending";
            SetCondition(entity, "Ready", "False", "NoTargetsFound", "No targets matched the selector");
        }
        else if (failures.Count == 0)
        {
            entity.Status.Phase = "Ready";
            entity.Status.AppliedLoginName = login.Name; // Persisted rename anchor
            SetCondition(entity, "Ready", "True", "UserConfigured", $"All {provisioned} target(s) provisioned");
        }
        else
        {
            entity.Status.Phase = provisioned > 0 ? "PartiallyProvisioned" : "Degraded";
            SetCondition(entity, "Ready", "False", "PartialError",
                $"Provisioned {provisioned}/{resolvedTargets.Count} target(s); {failures.Count} failed: {string.Join("; ", failures)}");
        }

        metrics.SetTargets("SqlUser", ns, name, provisioned, resolvedTargets.Count, failures.Count);

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        // Retry failed targets on the next loop; do not pin a long success requeue over a failure.
        if (failures.Count > 0)
            return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromSeconds(30));

        if (requeueAfter.HasValue)
            return ReconciliationResult<SqlUser>.Success(entity, requeueAfter.Value);

        return ReconciliationResult<SqlUser>.Success(entity);
    }

    public Task<ReconciliationResult<SqlUser>> DeletedAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("SqlUser {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlUser", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
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

        // Reactivation: if the expiration was pushed into the future, clear expired state.
        // RevokedAt is intentionally left set so the reconcile loop knows it must re-enable the
        // login on every target before treating the user as fully active again.
        if (entity.Status.Expired)
        {
            entity.Status.Expired = false;
        }

        return effectiveExpiresAt.Value - DateTime.UtcNow;
    }

    // ── Revocation ────────────────────────────────────────────

    /// <summary>
    /// Idempotently applies the configured expiration action (Disable/Delete) to every resolved
    /// target on each reconcile. There is no per-target status array anymore, so revocation
    /// progress is recomputed from scratch each pass: success/failure is tracked in local counters,
    /// <c>RevokedAt</c> is stamped only once every target has been handled, and the
    /// <c>RevocationPending</c> condition is the cross-reconcile source of truth that drives requeue.
    /// Disabling/dropping a login that is already disabled/absent is treated as success (idempotent).
    /// Returns <c>true</c> while at least one target still needs work.
    /// </summary>
    private async Task<bool> HandleRevocationAsync(SqlUser entity, string ns, string name, LoginSpec login, CancellationToken ct)
    {
        var action = entity.Spec.Expiration?.Action ?? "Disable";
        var terminateSessions = entity.Spec.Expiration?.TerminateSessions ?? true;
        var isDelete = action.Equals("Delete", StringComparison.OrdinalIgnoreCase);

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        var revoked = 0;
        var failures = new List<string>();
        var firstTransition = entity.Status.RevokedAt == null;

        foreach (var target in resolvedTargets)
        {
            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                foreach (var (connection, _) in connections)
                {
                    await using (connection)
                    {
                        if (terminateSessions)
                            await sqlUserManager.KillSessionsAsync(connection, login.Name, ct);

                        if (isDelete)
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

                            // Idempotent: dropping an absent login is a no-op success.
                            if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                                await sqlUserManager.DropLoginAsync(connection, login.Name, ct);
                        }
                        else
                        {
                            // Idempotent: disabling an absent/already-disabled login is success.
                            if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                                await sqlUserManager.DisableLoginAsync(connection, login.Name, ct);
                        }
                    }
                }

                revoked++;
            }
            catch (Exception ex)
            {
                failures.Add($"{target.Kind}/{target.Name}: {ex.Message}");
                logger.LogWarning(ex, "Revocation failed for SqlUser {Name} on {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "TargetUnreachable",
                    $"Revocation pending on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        var hasPending = failures.Count > 0;

        // Stamp the single RevokedAt only once every target has been handled.
        if (!hasPending)
            entity.Status.RevokedAt ??= DateTime.UtcNow;

        entity.Status.Phase = "Revoked";
        entity.Status.TotalTargets = resolvedTargets.Count;
        entity.Status.ReadyTargets = revoked;
        entity.Status.FailedTargets = failures.Count;
        metrics.SetTargets("SqlUser", ns, name, revoked, resolvedTargets.Count, failures.Count);

        // Emit the transition event only the first time we enter revocation.
        if (firstTransition && !hasPending)
        {
            var eventName = isDelete ? "UserDeleted" : "LoginRevoked";
            await eventPublisher.PublishAsync(entity, eventName,
                $"Login {login.Name} {action.ToLowerInvariant()}d on expired SqlUser", "Warning", ct);
        }

        SetCondition(entity, "Expired", "True", isDelete ? "UserDeleted" : "LoginRevoked",
            $"Expired at {entity.Status.EffectiveExpiresAt}");
        SetCondition(entity, "RevocationPending", hasPending ? "True" : "False",
            hasPending ? "PendingTargets" : "AllRevoked",
            hasPending ? $"{failures.Count}/{resolvedTargets.Count} target(s) not yet revoked: {string.Join("; ", failures)}" : null);

        return hasPending;
    }

    // ── Reactivation ──────────────────────────────────────────

    /// <summary>
    /// Re-enables the login on every resolved target idempotently. Recomputed each reconcile from
    /// the live server state (LoginExists / enable is a no-op if already enabled) rather than from a
    /// per-target status array. Clears <c>RevokedAt</c> so the resource returns to its normal
    /// provisioning path.
    /// </summary>
    private async Task HandleReactivationAsync(SqlUser entity, string ns, LoginSpec login, CancellationToken ct)
    {
        logger.LogInformation("Reactivating SqlUser {Name}", entity.Metadata.Name);

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        foreach (var target in resolvedTargets)
        {
            try
            {
                var connections = await ResolveTargetConnectionsAsync(target, ct);
                foreach (var (connection, _) in connections)
                {
                    await using (connection)
                    {
                        // Idempotent: EnableLogin on an already-enabled login is a no-op. If the
                        // login was dropped (Delete action), the normal provisioning path below
                        // will recreate it.
                        if (await sqlUserManager.LoginExistsAsync(connection, login.Name, ct))
                            await sqlUserManager.EnableLoginAsync(connection, login.Name, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reactivation failed for SqlUser {Name} on {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
            }
        }

        entity.Status.RevokedAt = null;

        SetCondition(entity, "Expired", "False", "Reactivated", "User reactivated with new expiration");
        SetCondition(entity, "RevocationPending", "False", "Reactivated", null);
        await eventPublisher.PublishAsync(entity, "LoginReactivated", $"Login {login.Name} reactivated", ct: ct);
    }

    // ── Provisioning ──────────────────────────────────────────

    private async Task ProvisionTargetAsync(SqlUser entity, ResolvedTarget target,
        TargetOverride? overrides, LoginSpec login, string loginPassword, CancellationToken ct)
    {
        var connections = await ResolveTargetConnectionsAsync(target, ct);
        byte[]? sid = null;

        // Privileged server roles are only granted when the
        // *target* opts in via annotation. The reconcile is the authority — the webhook only
        // warns (it can be disabled, and the annotation can change after admission).
        var requestedRoles = overrides?.ServerRoles ?? entity.Spec.ServerRoles;
        var deniedRoles = new List<string>();
        if (requestedRoles?.Any(r => SqlIdentifierRules.PrivilegedServerRoles.Contains(r)) == true
            && !await TargetAllowsPrivilegedRolesAsync(target, ct))
        {
            deniedRoles = requestedRoles.Where(r => SqlIdentifierRules.PrivilegedServerRoles.Contains(r)).Distinct().ToList();
            SetCondition(entity, "PrivilegedRolesDenied", "True", "TargetOptInMissing",
                $"Role(s) {string.Join(", ", deniedRoles)} not granted on {target.Kind}/{target.Name}: " +
                $"annotation {SqlIdentifierRules.AllowPrivilegedRolesAnnotation}=\"true\" missing on the target");
            await eventPublisher.PublishAsync(entity, "PrivilegedRolesDenied",
                $"Refused to grant {string.Join(", ", deniedRoles)} on {target.Kind}/{target.Name} without target opt-in annotation", "Warning", ct);
        }
        else
        {
            SetCondition(entity, "PrivilegedRolesDenied", "False", "NoPrivilegedRolesOrOptedIn", null);
        }

        // Create login on each connection (instance or AG replicas)
        var appliedName = entity.Status?.AppliedLoginName;
        foreach (var (connection, replicaName) in connections)
        {
            await using (connection)
            {
                // spec.login.name changed → native rename (SID preserved), never an
                // orphaned old login + fresh new one.
                if (appliedName != null
                    && !appliedName.Equals(login.Name, StringComparison.OrdinalIgnoreCase)
                    && !await sqlUserManager.LoginExistsAsync(connection, login.Name, ct)
                    && await sqlUserManager.LoginExistsAsync(connection, appliedName, ct))
                {
                    await sqlUserManager.RenameLoginAsync(connection, appliedName, login.Name, ct);
                    logger.LogInformation("Renamed login {Old} to {New} on {Target}", appliedName, login.Name, replicaName ?? target.Name);
                }

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

                // Server roles (override or global); privileged ones gated by target opt-in
                if (requestedRoles != null)
                    foreach (var role in requestedRoles.Where(r => !deniedRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                        await sqlUserManager.AddServerRoleMemberAsync(connection, login.Name, role, ct);

                // Server permissions (override or global)
                var serverPermissions = overrides?.ServerPermissions ?? entity.Spec.ServerPermissions;
                if (serverPermissions != null)
                    foreach (var perm in serverPermissions)
                        await sqlUserManager.GrantServerPermissionAsync(connection, login.Name, perm, ct);
            }
        }

        // Database permissions — through the actual PRIMARY: ordinal 0 is wrong
        // after any failover, and AG replication propagates database-level changes anyway.
        var dbPermissions = overrides?.DatabasePermissions ?? entity.Spec.DatabasePermissions;
        if (dbPermissions != null && connections.Count > 0)
        {
            var primaryConn = await OpenPrimaryConnectionAsync(target, ct);
            if (primaryConn == null)
            {
                throw new InvalidOperationException($"No primary replica reachable on {target.Kind}/{target.Name}; database permissions not applied.");
            }
            {
                await using var dbConn = primaryConn;
                var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(dbConn, ct);

                foreach (var dbPerm in dbPermissions)
                {
                    var targetDbs = databaseFilter.Apply(allDatabases, dbPerm.Databases, dbPerm.DatabaseFilter);
                    foreach (var db in targetDbs)
                    {
                        if (!await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                            await sqlUserManager.CreateDatabaseUserAsync(dbConn, db, login.Name, login.Name, ct);

                        if (dbPerm.Roles != null)
                        {
                            foreach (var role in dbPerm.Roles)
                                await sqlUserManager.AddRoleMemberAsync(dbConn, db, login.Name, role, ct);
                        }

                        if (dbPerm.SchemaPermissions != null)
                        {
                            foreach (var sp in dbPerm.SchemaPermissions)
                                foreach (var perm in sp.Permissions)
                                    await sqlUserManager.GrantSchemaPermissionAsync(dbConn, db, login.Name, sp.Schema, perm, ct);
                        }
                    }
                }
            }
        }
    }

    // ── Connection resolution ─────────────────────────────────

    /// <summary>Opens a connection to the target's writable endpoint: the live PRIMARY for an
    /// AG (never ordinal 0), the instance itself otherwise. Null when unreachable.</summary>
    private async Task<Microsoft.Data.SqlClient.SqlConnection?> OpenPrimaryConnectionAsync(ResolvedTarget target, CancellationToken ct)
    {
        if (target.Kind == "SqlAvailabilityGroup")
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(
                target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
            return primary?.Connection;
        }

        return await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
    }

    private async Task<List<(Microsoft.Data.SqlClient.SqlConnection Connection, string? ReplicaName)>> ResolveTargetConnectionsAsync(ResolvedTarget target, CancellationToken ct)
    {
        var results = new List<(Microsoft.Data.SqlClient.SqlConnection, string?)>();

        try
        {
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
        }
        catch
        {
            // Failing on replica N must not leak the N-1 connections already opened.
            foreach (var (conn, _) in results)
                await conn.DisposeAsync();
            throw;
        }

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────

    private async Task<string?> ResolveLoginPasswordAsync(LoginSpec login, string ns, CancellationToken ct)
    {
        if (login.PasswordSecretRef == null) return null;
        return await ResolveSecretValueAsync(login.PasswordSecretRef, ns, ct);
    }

    private Task<string?> ResolveSecretValueAsync(SecretReference secretRef, string ns, CancellationToken ct)
        // Null only on 404; transport errors propagate (runtime backoff).
        => SecretValues.TryGetAsync(kubernetesClient, secretRef, ns, ct);

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

    /// <summary>Reads the opt-in annotation on the resolved target CR (fresh GET — the
    /// annotation may change at any time, the reconcile must see the current value).</summary>
    private async Task<bool> TargetAllowsPrivilegedRolesAsync(ResolvedTarget target, CancellationToken ct)
    {
        IDictionary<string, string>? annotations = target.Kind switch
        {
            "SqlInstance" => (await kubernetesClient.GetAsync<SqlInstance>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            "SqlAvailabilityGroup" => (await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            _ => null,
        };
        return SqlIdentifierRules.AllowsPrivilegedRoles(annotations);
    }

    private static void SetCondition(SqlUser entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);
}
