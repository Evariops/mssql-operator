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
    OperatorMetrics metrics,
    TimeProvider? timeProvider = null) : IEntityController<SqlUser>
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ReconciliationResult<SqlUser>> ReconcileAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlUser {Name}", entity.Metadata.Name);

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

        // Defence in depth — the webhook rejects reserved logins, but it can be disabled
        // or set to failurePolicy=Ignore, and this path rewrites the password of whatever login it is
        // given. Rewriting 'sa' locks the operator out of the instance permanently.
        if (SqlIdentifierRules.CheckNotReserved(login.Name, "login.name") is { } reserved)
        {
            logger.LogError("SqlUser {Name} targets the reserved login {Login}; refusing", entity.Metadata.Name, login.Name);
            await eventPublisher.PublishAsync(entity, "ReservedLoginRefused", reserved, "Warning", ct);
            entity.Status.Phase = Phases.Failed;
            SetCondition(entity, "Ready", "False", "ReservedLogin", reserved);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlUser>.Success(entity, TimeSpan.FromMinutes(10));
        }

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
            entity.Status.Phase = Phases.Pending;
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
                // The target only — the exception text (durations, counters) changes on every
                // pass and would rewrite the status each time; it goes to the event and the logs.
                failures.Add($"{target.Kind}/{target.Name}");
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
            entity.Status.Phase = Phases.Pending;
            SetCondition(entity, "Ready", "False", "NoTargetsFound", "No targets matched the selector");
        }
        else if (failures.Count == 0)
        {
            entity.Status.Phase = Phases.Ready;
            entity.Status.AppliedLoginName = login.Name; // Persisted rename anchor
            SetCondition(entity, "Ready", "True", "UserConfigured", $"All {provisioned} target(s) provisioned");
        }
        else
        {
            entity.Status.Phase = provisioned > 0 ? "PartiallyProvisioned" : "Degraded";
            SetCondition(entity, "Ready", "False", "PartialError",
                $"Provisioned {provisioned}/{resolvedTargets.Count} target(s); {failures.Count} failed: {string.Join(", ", failures)} (see events)");
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

        if (_clock.GetUtcNow().UtcDateTime >= effectiveExpiresAt.Value)
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

        return effectiveExpiresAt.Value - _clock.GetUtcNow().UtcDateTime;
    }

    // ── Revocation ────────────────────────────────────────────

    /// <summary>
    /// True when this CR actually provisioned the login in SQL Server. The operator only
    /// ever destroys what it created: a pre-existing login that merely shares the name belongs to a DBA,
    /// and dropping it takes an application down.
    /// </summary>
    internal static bool OperatorProvisioned(SqlUser entity, LoginSpec login)
        => string.Equals(entity.Status?.AppliedLoginName, login.Name, StringComparison.OrdinalIgnoreCase);

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

        // Only destroy a login the operator actually provisioned. Status.AppliedLoginName
        // is stamped when the login is created/renamed by this CR; when it is absent, the login in SQL
        // Server was created by someone else and merely shares the name — dropping it (and its database
        // users, with a forced schema transfer to dbo) destroys a principal the operator never owned.
        if (isDelete && !OperatorProvisioned(entity, login))
        {
            await eventPublisher.PublishAsync(entity, "RevocationSkippedNotOwned",
                $"Expiration action Delete was skipped for login '{login.Name}': this SqlUser never provisioned it " +
                "(status.appliedLoginName is not set), so it belongs to someone else. Disable/drop it manually if intended.",
                "Warning", ct);
            SetCondition(entity, "RevocationPending", "False", "NotOwned", "Login was not provisioned by this resource");
            entity.Status.RevokedAt ??= _clock.GetUtcNow().UtcDateTime;
            return false;
        }

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        var revoked = 0;
        var failures = new List<string>();
        var firstTransition = entity.Status.RevokedAt == null;

        foreach (var target in resolvedTargets)
        {
            try
            {
                var connections = await TargetConnections.OpenEveryServerAsync(target, connectionFactory, kubernetesClient, ct);
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
                                    catch (Microsoft.Data.SqlClient.SqlException)
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
                failures.Add($"{target.Kind}/{target.Name}");
                logger.LogWarning(ex, "Revocation failed for SqlUser {Name} on {Kind}/{Target}", entity.Metadata.Name, target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "TargetUnreachable",
                    $"Revocation pending on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        var hasPending = failures.Count > 0;

        // Stamp the single RevokedAt only once every target has been handled.
        if (!hasPending)
            entity.Status.RevokedAt ??= _clock.GetUtcNow().UtcDateTime;

        entity.Status.Phase = Phases.Revoked;
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
            hasPending ? $"{failures.Count}/{resolvedTargets.Count} target(s) not yet revoked: {string.Join(", ", failures)} (see events)" : null);

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
                var connections = await TargetConnections.OpenEveryServerAsync(target, connectionFactory, kubernetesClient, ct);
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
        var targetKey = $"{target.Kind}/{target.Name}";

        // Privileged server roles are only granted when the *target*
        // opts in via annotation. The reconcile is the authority — the webhook only warns (it can be
        // disabled, and the annotation can change after admission). The same gate covers the
        // server permissions that amount to the same power (CONTROL SERVER, IMPERSONATE ANY LOGIN…)
        // and every fixed server role, not just sysadmin/securityadmin/serveradmin.
        var requestedRoles = (overrides?.ServerRoles ?? entity.Spec.ServerRoles ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var requestedPermissions = (overrides?.ServerPermissions ?? entity.Spec.ServerPermissions ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var privileged = requestedRoles.Where(SqlIdentifierRules.PrivilegedServerRoles.Contains)
            .Concat(requestedPermissions.Where(SqlIdentifierRules.PrivilegedServerPermissions.Contains))
            .ToList();
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (privileged.Count > 0 && !await TargetAllowsPrivilegedRolesAsync(target, ct))
        {
            denied.UnionWith(privileged);
            SetCondition(entity, "PrivilegedRolesDenied", "True", "TargetOptInMissing",
                $"Privileged grant(s) {string.Join(", ", privileged)} not granted on {targetKey}: " +
                $"annotation {SqlIdentifierRules.AllowPrivilegedRolesAnnotation}=\"true\" missing on the target");
            await eventPublisher.PublishAsync(entity, "PrivilegedRolesDenied",
                $"Refused to grant {string.Join(", ", privileged)} on {targetKey} without target opt-in annotation", "Warning", ct);
        }
        else
        {
            SetCondition(entity, "PrivilegedRolesDenied", "False", "NoPrivilegedRolesOrOptedIn", null);
        }

        var roles = requestedRoles.Where(r => !denied.Contains(r)).ToList();
        var permissions = requestedPermissions.Where(p => !denied.Contains(p)).ToList();

        // What this resource granted on this target in earlier passes. Anything recorded there
        // that the spec no longer asks for is revoked below; the ledger only ever holds grants the
        // operator itself made, so a DBA's own grants are never touched.
        var ledger = GrantLedger.For(entity.Status!.AppliedGrants, targetKey);

        // Create login on each connection (instance or AG replicas).
        // Connections are opened eagerly for every replica, so a SQL error in the middle
        // of the loop leaked all the ones the `await using` had not reached yet. A finally disposes
        // them all, on every path.
        var connections = await TargetConnections.OpenEveryServerAsync(target, connectionFactory, kubernetesClient, ct);
        var appliedName = entity.Status?.AppliedLoginName;
        try
        {
            // One SID on every replica, decided BEFORE any login is created. It used to be read
            // from ordinal 0 after creating the login there: a rebuilt replica 0 minted a fresh SID that
            // the other replicas (and every database user) did not have, and users were orphaned after
            // the next failover.
            var sid = target.IsAvailabilityGroup
                ? await ResolveAgLoginSidAsync(entity, target, connections, login.Name, ct)
                : null;

            // Server-level grants made earlier and no longer wanted (removed from the spec, or no longer
            // allowed because the target's opt-in was withdrawn) are revoked on every replica.
            // The desired ones are recorded before granting, so a pass failing half-way still knows
            // what it may have granted.
            var staleRoles = ledger.Stale(GrantLedger.ServerRole, roles);
            var stalePermissions = ledger.Stale(GrantLedger.ServerPermission, permissions);
            ledger.AddRange(GrantLedger.ServerRole, roles);
            ledger.AddRange(GrantLedger.ServerPermission, permissions);

            foreach (var (connection, replicaName) in connections)
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
                    logger.LogInformation("Created login {Login} on {Target}", login.Name, replicaName ?? targetKey);
                }
                else if (!await sqlUserManager.PasswordMatchesAsync(connection, login.Name, loginPassword, ct))
                {
                    // Secret rotation. Only when the password really differs — an ALTER on every
                    // pass reset the password age and made CHECK_EXPIRATION meaningless.
                    await sqlUserManager.AlterLoginPasswordAsync(connection, login.Name, loginPassword, ct);
                }

                // The first login created on an AG fixes the SID for the remaining replicas.
                if (sid == null && target.IsAvailabilityGroup)
                    sid = await sqlUserManager.GetLoginSidAsync(connection, login.Name, ct);

                foreach (var role in roles)
                    await sqlUserManager.AddServerRoleMemberAsync(connection, login.Name, role, ct);
                foreach (var perm in permissions)
                    await sqlUserManager.GrantServerPermissionAsync(connection, login.Name, perm, ct);

                foreach (var role in staleRoles)
                    await sqlUserManager.DropServerRoleMemberAsync(connection, login.Name, role, ct);
                foreach (var perm in stalePermissions)
                    await sqlUserManager.RevokeServerPermissionAsync(connection, login.Name, perm, ct);
            }

            ledger.Replace(GrantLedger.ServerRole, roles);
            ledger.Replace(GrantLedger.ServerPermission, permissions);

            // Database permissions — through the actual PRIMARY: ordinal 0 is wrong
            // after any failover, and AG replication propagates database-level changes anyway.
            var dbPermissions = overrides?.DatabasePermissions ?? entity.Spec.DatabasePermissions ?? [];
            if (connections.Count > 0 && (dbPermissions.Count > 0 || ledger.HasDatabaseGrants))
            {
                var primaryConn = await TargetConnections.OpenPrimaryAsync(target, connectionFactory, targetResolver, ct)
                    ?? throw new InvalidOperationException($"No primary replica reachable on {targetKey}; database permissions not applied.");
                await using var dbConn = primaryConn;
                await ApplyDatabaseGrantsAsync(dbConn, login, dbPermissions, ledger, ct);
            }
        }
        finally
        {
            // Dispose every eagerly-opened connection, including those the loop never
            // reached because an earlier replica threw.
            foreach (var (connection, _) in connections)
                await connection.DisposeAsync();

            entity.Status!.AppliedGrants = ledger.Merge(entity.Status.AppliedGrants);
        }
    }

    /// <summary>
    /// Grants the database-level permissions of the spec on the writable endpoint, then revokes the
    /// ones this resource granted earlier and the spec no longer asks for — including the
    /// whole database user of a database that left the scope, when the operator created it.
    /// </summary>
    private async Task ApplyDatabaseGrantsAsync(Microsoft.Data.SqlClient.SqlConnection dbConn, LoginSpec login,
        List<DatabasePermissionSpec> dbPermissions, GrantLedger ledger, CancellationToken ct)
    {
        var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(dbConn, ct);
        var users = new List<string>();
        var roles = new List<string>();
        var schemaGrants = new List<string>();

        foreach (var dbPerm in dbPermissions)
        {
            // An entry without databases nor databaseFilter used to mean "every user database".
            if (dbPerm.Databases is not { Count: > 0 } && dbPerm.DatabaseFilter is null)
                throw new InvalidOperationException(
                    "A databasePermissions entry names neither databases nor a databaseFilter; refusing to apply it to every database.");

            foreach (var db in databaseFilter.Apply(allDatabases, dbPerm.Databases, dbPerm.DatabaseFilter))
            {
                if (!await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                {
                    await sqlUserManager.CreateDatabaseUserAsync(dbConn, db, login.Name, login.Name, ct);
                    ledger.Add(GrantLedger.DatabaseUser, db);
                }

                if (ledger.Contains(GrantLedger.DatabaseUser, db))
                    users.Add(db);

                foreach (var role in dbPerm.Roles ?? [])
                {
                    var grant = GrantLedger.Join(db, role);
                    ledger.Add(GrantLedger.DatabaseRole, grant);
                    await sqlUserManager.AddRoleMemberAsync(dbConn, db, login.Name, role, ct);
                    roles.Add(grant);
                }

                foreach (var sp in dbPerm.SchemaPermissions ?? [])
                    foreach (var perm in sp.Permissions)
                    {
                        var grant = GrantLedger.Join(db, sp.Schema, perm.ToUpperInvariant());
                        ledger.Add(GrantLedger.SchemaPermission, grant);
                        await sqlUserManager.GrantSchemaPermissionAsync(dbConn, db, login.Name, sp.Schema, perm, ct);
                        schemaGrants.Add(grant);
                    }
            }
        }

        var existing = new HashSet<string>(allDatabases, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in ledger.Stale(GrantLedger.DatabaseRole, roles))
        {
            var (db, role) = GrantLedger.Split2(stale);
            if (existing.Contains(db) && await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                await sqlUserManager.DropRoleMemberAsync(dbConn, db, login.Name, role, ct);
        }

        foreach (var stale in ledger.Stale(GrantLedger.SchemaPermission, schemaGrants))
        {
            var (db, schema, perm) = GrantLedger.Split3(stale);
            if (existing.Contains(db) && await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                await sqlUserManager.RevokeSchemaPermissionAsync(dbConn, db, login.Name, schema, perm, ct);
        }

        foreach (var db in ledger.Stale(GrantLedger.DatabaseUser, users))
        {
            if (!existing.Contains(db) || !await sqlUserManager.DatabaseUserExistsAsync(dbConn, db, login.Name, ct))
                continue;
            try
            {
                await sqlUserManager.DropDatabaseUserAsync(dbConn, db, login.Name, ct);
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // The user owns a schema: hand it to dbo, then drop.
                await sqlUserManager.TransferSchemaOwnershipAsync(dbConn, db, login.Name, ct);
                await sqlUserManager.DropDatabaseUserAsync(dbConn, db, login.Name, ct);
            }
        }

        ledger.Replace(GrantLedger.DatabaseUser, users);
        ledger.Replace(GrantLedger.DatabaseRole, roles);
        ledger.Replace(GrantLedger.SchemaPermission, schemaGrants);
    }

    /// <summary>
    /// The SID the login must carry on every replica of an AG: the one it already has where
    /// it exists, the primary's when replicas disagree (database users are mapped to the primary's
    /// SID and replicated). A disagreement is reported: it orphans database users after a failover
    /// and is only fixed by recreating the login on the diverging replica.
    /// </summary>
    private async Task<byte[]?> ResolveAgLoginSidAsync(SqlUser entity, ResolvedTarget target,
        List<(Microsoft.Data.SqlClient.SqlConnection Connection, string? ReplicaName)> connections, string loginName, CancellationToken ct)
    {
        var sids = new List<(string? Replica, byte[] Sid)>();
        foreach (var (connection, replicaName) in connections)
            if (await sqlUserManager.GetLoginSidAsync(connection, loginName, ct) is { } existing)
                sids.Add((replicaName, existing));

        var distinct = sids.Select(s => Convert.ToHexString(s.Sid)).Distinct().ToList();
        if (distinct.Count <= 1)
            return sids.Count == 0 ? null : sids[0].Sid;

        var primary = await targetResolver.FindPrimaryReplicaAsync(
            target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
        byte[]? primarySid = null;
        if (primary is { } p)
        {
            await using var primaryConn = p.Connection;
            primarySid = await sqlUserManager.GetLoginSidAsync(primaryConn, loginName, ct);
        }

        var diverging = sids.Where(s => primarySid is null || !s.Sid.AsSpan().SequenceEqual(primarySid)).Select(s => s.Replica);
        var message =
            $"Login '{loginName}' has different SIDs across the replicas of {target.Kind}/{target.Name} " +
            $"(diverging: {string.Join(", ", diverging)}). Database users are orphaned on those replicas after a failover: " +
            "drop the login there and let the operator recreate it with the primary's SID.";
        SetCondition(entity, "LoginSidMismatch", "True", "SidMismatch", message);
        await eventPublisher.PublishAsync(entity, "LoginSidMismatch", message, "Warning", ct);
        return primarySid ?? sids[0].Sid;
    }

    // ── Connection resolution ─────────────────────────────────

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
            TargetKinds.Instance => entity.Spec.Overrides.FirstOrDefault(o => o.Instance == target.Name),
            TargetKinds.AvailabilityGroup => entity.Spec.Overrides.FirstOrDefault(o => o.AvailabilityGroup == target.Name),
            _ => null
        };
    }

    /// <summary>Reads the opt-in annotation on the resolved target CR (fresh GET — the
    /// annotation may change at any time, the reconcile must see the current value).</summary>
    private async Task<bool> TargetAllowsPrivilegedRolesAsync(ResolvedTarget target, CancellationToken ct)
    {
        var annotations = await TargetConnections.GetAnnotationsAsync(target, kubernetesClient, ct);
        return SqlIdentifierRules.AllowsPrivilegedRoles(annotations);
    }

    private static void SetCondition(SqlUser entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);
}
