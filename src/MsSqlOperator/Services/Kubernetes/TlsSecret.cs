using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>The Secret a <see cref="TlsSpec"/> names must exist before the pods mount it — a
/// missing one leaves them in ContainerCreating with nothing on the resource to say why.</summary>
public static class TlsSecret
{
    /// <summary>Why the Secret cannot be mounted, or null (no TLS, or a usable Secret). API errors
    /// propagate: only an absent Secret or key is reported as such.</summary>
    public static async Task<string?> MissingAsync(IKubernetesClient client, TlsSpec? tls, string ns, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tls?.SecretName))
            return null;
        var secret = await client.GetAsync<V1Secret>(tls.SecretName, ns, ct);
        if (secret is null)
            return $"TLS Secret {ns}/{tls.SecretName} not found";
        return secret.Data is { } data && data.ContainsKey("tls.crt") && data.ContainsKey("tls.key")
            ? null
            : $"TLS Secret {ns}/{tls.SecretName} lacks tls.crt or tls.key";
    }
}
