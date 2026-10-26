using k8s;
using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Kubernetes;

public enum SaConvergence
{
    /// <summary>Every pod accepts the Secret's password, and it is recorded as applied.</summary>
    Converged,

    /// <summary>Some pod is not running or not reachable yet: retried on the next pass.</summary>
    Pending,

    /// <summary>A pod refuses both the Secret's password and the last applied one: manual action needed.</summary>
    Failed,
}

/// <summary>
/// Converges the <c>sa</c> login of every pod of a SqlInstance or SqlAvailabilityGroup to the value of
/// the referenced Secret.
///
/// The image reads <c>MSSQL_SA_PASSWORD</c> only on FIRST boot, so a rotated Secret leaves SQL Server
/// on the old password. The operator authenticates with the password it last applied (kept in the
/// retained Secret <c>{name}-sa-applied</c>) and runs ALTER LOGIN on each pod, through the pod IP: a
/// NotReady pod has no Service endpoint, but its IP answers. Availability groups had no convergence at
/// all: after a rotation the operator could not log into any replica, and a restarted replica never
/// became Ready again (its environment carried the new value, SQL Server the old one).
/// </summary>
public interface ISaPasswordConverger
{
    Task<SaConvergence> ConvergeAsync(IKubernetesObject<V1ObjectMeta> owner, IReadOnlyList<string> pods, string saPassword, CancellationToken ct);
}

public class SaPasswordConverger(
    ILogger<SaPasswordConverger> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    IEventPublisher eventPublisher) : ISaPasswordConverger
{
    /// <summary>SQL Server error 18456: login failed.</summary>
    private const int LoginFailedError = 18456;

    public static string AppliedSecretName(string owner) => $"{owner}-sa-applied";

    public async Task<SaConvergence> ConvergeAsync(IKubernetesObject<V1ObjectMeta> owner, IReadOnlyList<string> pods, string saPassword, CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty;
        var name = owner.Metadata.Name;

        // Steady state: the Secret holds what SQL Server was last given — no SQL round-trip at all.
        var applied = await ReadAppliedAsync(ns, name, ct);
        if (applied == saPassword)
            return SaConvergence.Converged;

        var outcome = SaConvergence.Converged;
        foreach (var podName in pods)
        {
            var pod = await kubernetesClient.GetAsync<V1Pod>(podName, ns, ct);
            if (pod?.Status?.PodIP is not { Length: > 0 } podIp)
            {
                outcome = Worst(outcome, SaConvergence.Pending);
                continue;
            }

            var certificateName = CertificateName(owner, podName);
            var accepted = await TryLoginAsync(podIp, certificateName, saPassword, ct);
            if (accepted == true)
                continue; // first boot, or already converged
            if (accepted is null)
            {
                outcome = Worst(outcome, SaConvergence.Pending);
                continue;
            }

            if (applied is null)
            {
                await eventPublisher.PublishAsync(owner, "SaPasswordConvergenceFailed",
                    $"Pod {podName} does not accept the password of the referenced Secret, and the operator has no record of the " +
                    "previously applied one (rotated before this operator version, or changed out of band). Set sa to the Secret's " +
                    "value manually (ALTER LOGIN [sa] WITH PASSWORD = …).", "Warning", ct);
                outcome = Worst(outcome, SaConvergence.Failed);
                continue;
            }

            try
            {
                await using var conn = await connectionFactory.CreateProbeConnectionAsync(
                    podIp, SqlEndpoints.ContainerPort, applied, "master", ct, certificateName);
                await sqlUserManager.AlterLoginPasswordAsync(conn, "sa", saPassword, ct);
                await eventPublisher.PublishAsync(owner, "SaPasswordRotated",
                    $"The sa login of {podName} was converged to the rotated Secret value", ct: ct);
            }
            catch (SqlException ex) when (ex.Number == LoginFailedError)
            {
                await eventPublisher.PublishAsync(owner, "SaPasswordConvergenceFailed",
                    $"Pod {podName} accepts neither the Secret's sa password nor the one last applied by the operator. " +
                    "Set sa to the Secret's value manually (ALTER LOGIN [sa] WITH PASSWORD = …).", "Warning", ct);
                outcome = Worst(outcome, SaConvergence.Failed);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not converge the sa password of {Pod}; will retry", podName);
                outcome = Worst(outcome, SaConvergence.Pending);
            }
        }

        if (outcome == SaConvergence.Converged)
            await PersistAppliedAsync(owner, saPassword, ct);
        return outcome;
    }

    private static SaConvergence Worst(SaConvergence a, SaConvergence b) => (SaConvergence)Math.Max((int)a, (int)b);

    /// <summary>
    /// The name the pod's certificate is issued for (the pod FQDN on an AG's headless
    /// Service, the Service FQDN of a SqlInstance). The connection goes to the pod IP, which matches
    /// no SAN: with certificate validation on (Encrypt=Strict), every login failed the handshake,
    /// looked like an unreachable pod, and the convergence stayed Pending forever.
    /// </summary>
    private static string CertificateName(IKubernetesObject<V1ObjectMeta> owner, string pod)
    {
        var ns = owner.Metadata.NamespaceProperty;
        return owner is SqlAvailabilityGroup
            ? $"{pod}.{owner.Metadata.Name}-headless.{ns}.svc.cluster.local"
            : SqlEndpoints.ServiceFqdn(owner.Metadata.Name, ns);
    }

    /// <summary>True when the login succeeds, false when SQL Server refuses it, null when the pod
    /// does not answer (nothing can be concluded).</summary>
    private async Task<bool?> TryLoginAsync(string host, string certificateName, string password, CancellationToken ct)
    {
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(host, SqlEndpoints.ContainerPort, password, "master", ct, certificateName);
            return true;
        }
        catch (SqlException ex) when (ex.Number == LoginFailedError)
        {
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A refused certificate is not a pod that is down: say so where an operator will look.
            if (IsCertificateFailure(ex))
                logger.LogWarning(ex, "sa convergence: the certificate of {Host} was rejected (expected name {Name})", host, certificateName);
            else
                logger.LogDebug(ex, "sa convergence: {Host} not reachable", host);
            return null;
        }
    }

    private static bool IsCertificateFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException)
                return true;
        return false;
    }

    private async Task<string?> ReadAppliedAsync(string ns, string name, CancellationToken ct)
    {
        var secret = await kubernetesClient.GetAsync<V1Secret>(AppliedSecretName(name), ns, ct);
        return secret?.Data is { } data && data.TryGetValue("password", out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;
    }

    /// <summary>Records the sa password SQL Server actually has, so a later rotation of the referenced
    /// Secret is convergeable. Retained like the other key material: the data on the PVCs
    /// is protected by it, so it must outlive the CR.</summary>
    private async Task PersistAppliedAsync(IKubernetesObject<V1ObjectMeta> owner, string saPassword, CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty;
        var secretName = AppliedSecretName(owner.Metadata.Name);
        var data = new Dictionary<string, byte[]> { ["password"] = System.Text.Encoding.UTF8.GetBytes(saPassword) };

        var existing = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);
        if (existing is null)
        {
            await kubernetesClient.CreateAsync(KeyMaterialSecrets.Build(secretName, ns, owner, data), ct);
            return;
        }

        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"Secret {ns}/{secretName}");
        existing.Data = data;
        await kubernetesClient.UpdateAsync(existing, ct);
    }
}
