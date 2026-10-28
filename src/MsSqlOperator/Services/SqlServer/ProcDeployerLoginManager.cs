using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Provisions the least-privilege login used to deploy stored procedures. The login
/// is a capability of the target SqlInstance/SqlAvailabilityGroup (one per instance, shared by
/// all SqlStoredProcedure CRs targeting it). It is a member of <c>db_ddladmin</c> on the
/// databases a CR targets — nothing more — so a compromised procedure body cannot take over the
/// server through the deployment path.
///
/// The membership is now held only for the duration of a deployment (a lease), instead of
/// accumulating on every database ever targeted. Leases on one target are serialized in-process —
/// only the leader reconciles — so one deployment never loses its rights to another one's release.
/// </summary>
public interface IProcDeployerLoginManager
{
    /// <summary>The fixed login name; safe to use directly as the connection UserID.</summary>
    string LoginName { get; }

    /// <summary>
    /// Ensures the deployer login exists on the target with the operator-managed password (on every
    /// AG replica, with one SID), and grants it <c>db_ddladmin</c> on <paramref name="databases"/>
    /// until the returned lease is disposed. <paramref name="saConnection"/> is an open sa connection
    /// to the primary; it must stay open until the lease is disposed.
    /// </summary>
    Task<IProcDeployerLease> AcquireAsync(ResolvedTarget target, SqlConnection saConnection, IReadOnlyList<string> databases, CancellationToken ct);
}

/// <summary>Deployment rights of the deployer login; disposing revokes them.</summary>
public interface IProcDeployerLease : IAsyncDisposable
{
    /// <summary>Password of the deployer login, for the deploy connection.</summary>
    string Password { get; }
}

public class ProcDeployerLoginManager(
    ILogger<ProcDeployerLoginManager> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager,
    MsSqlOperator.Services.ConnectionFactory.TargetLimiter? targetLimiter = null) : IProcDeployerLoginManager
{
    public string LoginName => "mssqlop_proc_deployer";

    private const string DdlAdminRole = "db_ddladmin";
    private const string SecretKey = "password";
    // Former rotation marker: an unsalted hash of the password in the Secret's metadata. Rotation is
    // now detected with PWDCOMPARE on each replica; the leftover annotation is removed.
    private const string LegacyHashAnnotation = "mssql-operator.evariops.com/proc-deployer-hash";
    private const string ArgoKeepAnnotation = "argocd.argoproj.io/sync-options";

    /// <summary>The shared per-target limiter, one slot for the deployer of a target (its
    /// password is rotated under it); a private one when none is injected (tests).</summary>
    private static readonly MsSqlOperator.Services.ConnectionFactory.TargetLimiter OwnLimiter = new();
    private readonly ConcurrentDictionary<string, byte> _legacyCleaned = new();

    public async Task<IProcDeployerLease> AcquireAsync(ResolvedTarget target, SqlConnection saConnection, IReadOnlyList<string> databases, CancellationToken ct)
    {
        var key = $"{target.Kind}/{target.Namespace}/{target.Name}";
        var gate = await (targetLimiter ?? OwnLimiter).AcquireAsync($"deployer/{key}", ct, slots: 1);
        try
        {
            var password = await GetOrCreatePasswordSecretAsync(target, ct);
            await EnsureLoginAsync(target, saConnection, password, ct);

            // Memberships granted by earlier versions stayed on every database ever targeted: drop
            // them once per target and process lifetime.
            if (_legacyCleaned.TryAdd(key, 0))
                await RevokeStaleMembershipsAsync(saConnection, databases, ct);

            // Database user + db_ddladmin membership per targeted database (primary; AG replicates).
            foreach (var db in databases)
            {
                if (!await sqlUserManager.DatabaseUserExistsAsync(saConnection, db, LoginName, ct))
                    await sqlUserManager.CreateDatabaseUserAsync(saConnection, db, LoginName, LoginName, ct);
                await sqlUserManager.AddRoleMemberAsync(saConnection, db, LoginName, DdlAdminRole, ct);
            }

            return new Lease(this, gate, saConnection, databases.ToList(), password);
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }
    }

    /// <summary>The login on the primary, then on every AG replica with the primary's SID; the
    /// password is converged wherever it differs (a rotation used to reach the primary only,
    /// so deployments failed after the next failover).</summary>
    private async Task EnsureLoginAsync(ResolvedTarget target, SqlConnection saConnection, string password, CancellationToken ct)
    {
        await EnsureLoginOnAsync(saConnection, password, sid: null, target.Name, ct);
        if (!target.IsAvailabilityGroup)
            return;

        var sid = await sqlUserManager.GetLoginSidAsync(saConnection, LoginName, ct);
        var replicaCount = target.ReplicaCount ?? 3;
        for (var i = 0; i < replicaCount; i++)
        {
            var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);
            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, SqlEndpoints.ContainerPort, target.SaPassword, "master", ct);
                await EnsureLoginOnAsync(conn, password, sid, $"{target.Name}-{i}", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A replica that is briefly unreachable is reconciled on the next deployment; the
                // deploy itself targets the primary.
                logger.LogWarning(ex, "Could not ensure deployer login on AG replica {Replica}", $"{target.Name}-{i}");
            }
        }
    }

    private async Task EnsureLoginOnAsync(SqlConnection connection, string password, byte[]? sid, string where, CancellationToken ct)
    {
        if (!await sqlUserManager.LoginExistsAsync(connection, LoginName, ct))
        {
            await sqlUserManager.CreateLoginAsync(connection, LoginName, password, defaultDatabase: null,
                checkPolicy: true, checkExpiration: false, sid: sid, ct);
            logger.LogInformation("Created stored-procedure deployer login on {Target}", where);
        }
        else if (!await sqlUserManager.PasswordMatchesAsync(connection, LoginName, password, ct))
        {
            await sqlUserManager.AlterLoginPasswordAsync(connection, LoginName, password, ct);
            logger.LogInformation("Rotated stored-procedure deployer login password on {Target}", where);
        }
    }

    private async Task RevokeStaleMembershipsAsync(SqlConnection saConnection, IReadOnlyList<string> keep, CancellationToken ct)
    {
        foreach (var db in await sqlServerManager.GetDatabaseNamesAsync(saConnection, ct))
        {
            if (keep.Contains(db, StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                if (await sqlUserManager.DatabaseUserExistsAsync(saConnection, db, LoginName, ct)
                    && (await sqlUserManager.GetDatabaseRoleMembershipsAsync(saConnection, db, LoginName, ct))
                        .Contains(DdlAdminRole, StringComparer.OrdinalIgnoreCase))
                    await sqlUserManager.DropRoleMemberAsync(saConnection, db, LoginName, DdlAdminRole, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not revoke the deployer's db_ddladmin membership on {Database}", db);
            }
        }
    }

    private async Task ReleaseAsync(SqlConnection saConnection, List<string> databases)
    {
        // The release must happen even when the deployment was cancelled.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var db in databases)
        {
            try
            {
                await sqlUserManager.DropRoleMemberAsync(saConnection, db, LoginName, DdlAdminRole, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not revoke the deployer's db_ddladmin membership on {Database}; it is retried on the next deployment", db);
            }
        }
    }

    private sealed class Lease(ProcDeployerLoginManager owner, IAsyncDisposable gate, SqlConnection saConnection,
        List<string> databases, string password) : IProcDeployerLease
    {
        private int _disposed;

        public string Password => password;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                await owner.ReleaseAsync(saConnection, databases);
            }
            finally
            {
                await gate.DisposeAsync();
            }
        }
    }

    private async Task<string> GetOrCreatePasswordSecretAsync(ResolvedTarget target, CancellationToken ct)
    {
        var secretName = $"{target.Name}-proc-deployer";
        var existing = await kubernetesClient.GetAsync<V1Secret>(secretName, target.Namespace, ct);
        if (existing?.Data != null && existing.Data.TryGetValue(SecretKey, out var bytes))
        {
            if (existing.Metadata.Annotations?.Remove(LegacyHashAnnotation) == true)
            {
                try
                {
                    await kubernetesClient.UpdateAsync(existing, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Could not remove the legacy hash annotation of {Secret}", secretName);
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        var newPassword = GeneratePassword();
        var owner = await BuildOwnerRefAsync(target, ct);
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = target.Namespace,
                // Managed-by for inventory (Secrets are excluded from the owned-resource watch).
                Labels = new Dictionary<string, string> { [Operator.OperatorLabels.ManagedBy] = Operator.OperatorLabels.ManagedByValue },
                OwnerReferences = owner is null ? null : [owner],
                Annotations = new Dictionary<string, string> { [ArgoKeepAnnotation] = "Delete=false" },
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]> { [SecretKey] = Encoding.UTF8.GetBytes(newPassword) },
        };

        try
        {
            await kubernetesClient.CreateAsync(secret, ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Concurrent creation: re-read the winner's password.
            var winner = await kubernetesClient.GetAsync<V1Secret>(secretName, target.Namespace, ct);
            if (winner?.Data != null && winner.Data.TryGetValue(SecretKey, out var winnerBytes))
                return Encoding.UTF8.GetString(winnerBytes);
        }

        return newPassword;
    }

    private async Task<V1OwnerReference?> BuildOwnerRefAsync(ResolvedTarget target, CancellationToken ct)
    {
        // The login is a capability of the instance/AG: own the Secret by it so a delete GCs it.
        IKubernetesObject<V1ObjectMeta>? owner = target.Kind switch
        {
            "SqlInstance" => await kubernetesClient.GetAsync<SqlInstance>(target.Name, target.Namespace, ct),
            "SqlAvailabilityGroup" => await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct),
            _ => null,
        };
        if (owner is null) return null;
        return new V1OwnerReference
        {
            ApiVersion = owner.ApiVersion,
            Kind = owner.Kind,
            Name = owner.Metadata.Name,
            Uid = owner.Metadata.Uid,
            Controller = true,
            BlockOwnerDeletion = true,
        };
    }

    // CHECK_POLICY=ON requires complexity: base64 yields upper+lower+digit (3 categories);
    // the suffix guarantees a symbol and a fixed minimum length.
    private static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)).Replace('/', 'x').Replace('+', 'y') + "Aa1!#";
}
