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

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Provisions the least-privilege login used to deploy stored procedures. The login
/// is a capability of the target SqlInstance/SqlAvailabilityGroup (one per instance, shared by
/// all SqlStoredProcedure CRs targeting it). It is a member of <c>db_ddladmin</c> on the
/// databases a CR targets — nothing more — so a compromised procedure body cannot take over the
/// server through the deployment path.
/// </summary>
public interface IProcDeployerLoginManager
{
    /// <summary>The fixed login name; safe to use directly as the connection UserID.</summary>
    string LoginName { get; }

    /// <summary>
    /// Ensures the deployer login exists on the target (SID-consistent across AG replicas),
    /// that it has a database user + db_ddladmin membership on each of <paramref name="databases"/>,
    /// and that its password matches the operator-managed Secret. Returns the password for the
    /// deploy connection. <paramref name="saConnection"/> is an open sa connection to the
    /// primary; provisioning of replicas opens its own sa connections.
    /// </summary>
    Task<string> EnsureAsync(ResolvedTarget target, SqlConnection saConnection, IReadOnlyList<string> databases, CancellationToken ct);
}

public class ProcDeployerLoginManager(
    ILogger<ProcDeployerLoginManager> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager) : IProcDeployerLoginManager
{
    public string LoginName => "mssqlop_proc_deployer";

    private const string DdlAdminRole = "db_ddladmin";
    private const string SecretKey = "password";
    private const string HashAnnotation = "mssql-operator.evariops.com/proc-deployer-hash";
    private const string ArgoKeepAnnotation = "argocd.argoproj.io/sync-options";

    public async Task<string> EnsureAsync(ResolvedTarget target, SqlConnection saConnection, IReadOnlyList<string> databases, CancellationToken ct)
    {
        var (password, hash, hashChanged) = await GetOrCreatePasswordSecretAsync(target, ct);

        // 1. Server login on the primary (capture SID for AG replica consistency).
        byte[]? sid = null;
        if (!await sqlUserManager.LoginExistsAsync(saConnection, LoginName, ct))
        {
            await sqlUserManager.CreateLoginAsync(saConnection, LoginName, password, defaultDatabase: null,
                checkPolicy: true, checkExpiration: false, sid: null, ct);
            logger.LogInformation("Created stored-procedure deployer login on {Target}", target.Name);
        }
        else if (hashChanged)
        {
            // rotation: the Secret password changed → converge SQL to it.
            await sqlUserManager.AlterLoginPasswordAsync(saConnection, LoginName, password, ct);
            logger.LogInformation("Rotated stored-procedure deployer login password on {Target}", target.Name);
        }

        if (target.Kind == "SqlAvailabilityGroup")
        {
            sid = await sqlUserManager.GetLoginSidAsync(saConnection, LoginName, ct);
            await EnsureLoginOnReplicasAsync(target, password, sid, ct);
        }

        // 2. Database user + db_ddladmin membership per targeted database (primary; AG replicates).
        foreach (var db in databases)
        {
            if (!await sqlUserManager.DatabaseUserExistsAsync(saConnection, db, LoginName, ct))
                await sqlUserManager.CreateDatabaseUserAsync(saConnection, db, LoginName, LoginName, ct);

            var roles = await sqlUserManager.GetDatabaseRoleMembershipsAsync(saConnection, db, LoginName, ct);
            if (!roles.Contains(DdlAdminRole, StringComparer.OrdinalIgnoreCase))
                await sqlUserManager.AddRoleMemberAsync(saConnection, db, LoginName, DdlAdminRole, ct);
        }

        await PersistHashAsync(target, hash, ct);
        return password;
    }

    private async Task EnsureLoginOnReplicasAsync(ResolvedTarget target, string password, byte[]? sid, CancellationToken ct)
    {
        var replicaCount = target.ReplicaCount ?? 3;
        for (var i = 0; i < replicaCount; i++)
        {
            var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);
            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);
                if (!await sqlUserManager.LoginExistsAsync(conn, LoginName, ct))
                    await sqlUserManager.CreateLoginAsync(conn, LoginName, password, defaultDatabase: null,
                        checkPolicy: true, checkExpiration: false, sid: sid, ct);
            }
            catch (Exception ex)
            {
                // A replica that is briefly unreachable will be reconciled on the next pass; the
                // deploy targets the primary anyway. Log and continue (best-effort consistency).
                logger.LogWarning(ex, "Could not ensure deployer login on AG replica {Replica}", $"{target.Name}-{i}");
            }
        }
    }

    private async Task<(string Password, string Hash, bool HashChanged)> GetOrCreatePasswordSecretAsync(ResolvedTarget target, CancellationToken ct)
    {
        var secretName = $"{target.Name}-proc-deployer";
        var existing = await kubernetesClient.GetAsync<V1Secret>(secretName, target.Namespace, ct);
        if (existing?.Data != null && existing.Data.TryGetValue(SecretKey, out var bytes))
        {
            var pwd = Encoding.UTF8.GetString(bytes);
            string? appliedHash = existing.Metadata.Annotations != null && existing.Metadata.Annotations.TryGetValue(HashAnnotation, out var ah) ? ah : null;
            var hash = HashPassword(pwd);
            return (pwd, hash, appliedHash != hash);
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
            {
                var pwd = Encoding.UTF8.GetString(winnerBytes);
                return (pwd, HashPassword(pwd), true);
            }
        }

        // New secret → SQL has no matching login yet, so treat as "changed" to force creation.
        return (newPassword, HashPassword(newPassword), true);
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

    private async Task PersistHashAsync(ResolvedTarget target, string hash, CancellationToken ct)
    {
        var secretName = $"{target.Name}-proc-deployer";
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretName, target.Namespace, ct);
        if (secret is null) return;
        secret.Metadata.Annotations ??= new Dictionary<string, string>();
        if (secret.Metadata.Annotations.TryGetValue(HashAnnotation, out var current) && current == hash) return;
        secret.Metadata.Annotations[HashAnnotation] = hash;
        try
        {
            await kubernetesClient.UpdateAsync(secret, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist deployer-login hash annotation for {Target}", target.Name);
        }
    }

    // CHECK_POLICY=ON requires complexity: base64 yields upper+lower+digit (3 categories);
    // the suffix guarantees a symbol and a fixed minimum length.
    private static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(33)).Replace('/', 'x').Replace('+', 'y') + "Aa1!#";

    private static string HashPassword(string password)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)))[..16];
}
