using System.Text;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Single secret-resolution helper: returns null ONLY when the Secret or the key is
/// absent (404 semantics — <see cref="IKubernetesClient.GetAsync{T}"/> already maps 404 to null).
/// Transport, auth and throttling errors propagate so the runtime backs off — the previous
/// per-controller catch-alls turned every API hiccup into "secret missing" and acted on it.
/// </summary>
public static class SecretValues
{
    public static async Task<string?> TryGetAsync(IKubernetesClient client, SecretReference? secretRef, string ns, CancellationToken ct)
    {
        if (secretRef == null) return null;
        var secret = await client.GetAsync<V1Secret>(secretRef.Name, ns, ct);
        if (secret?.Data == null) return null;
        return secret.Data.TryGetValue(secretRef.Key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }
}
