using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using k8s;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Authenticates incoming aggregated-API requests as genuinely originating from the kube-aggregator
/// (the API server's front proxy), not a client that merely set <c>X-Remote-User</c> by hand.
///
/// The aggregator opens an mTLS connection presenting its front-proxy client certificate. We:
///   1. read the trust anchors and policy from the <c>extension-apiserver-authentication</c> ConfigMap
///      in kube-system (<c>requestheader-client-ca-file</c>, <c>requestheader-allowed-names</c>,
///      <c>requestheader-username-headers</c>, <c>requestheader-group-headers</c>);
///   2. validate the presented client certificate chains to that CA and (when allowed-names is set) its
///      CN is whitelisted;
///   3. only then trust the identity headers it forwarded.
///
/// Config is cached and refreshed on a TTL so a rotated front-proxy CA is picked up without a restart.
/// Requires Kestrel client-cert negotiation (<c>ClientCertificateMode=AllowCertificate</c> on the HTTPS
/// endpoint) — set by the Helm chart when the view API is enabled.
/// </summary>
public sealed class FrontProxyAuthenticator(IKubernetes client, ILogger<FrontProxyAuthenticator> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _cached;
    private DateTimeOffset _loadedAt;

    public sealed record AuthResult(bool Ok, string? User, List<string> Groups, int Status, string? Reason);

    public async Task<AuthResult> AuthenticateAsync(HttpContext ctx, CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        if (config is null)
            return new AuthResult(false, null, [], StatusCodes.Status403Forbidden, "front-proxy authentication config unavailable");

        var clientCert = await ctx.Connection.GetClientCertificateAsync(ct);
        if (clientCert is null)
            return new AuthResult(false, null, [], StatusCodes.Status401Unauthorized,
                "no client certificate (request did not arrive through the kube-aggregator)");

        if (!ChainsToCa(clientCert, config.CaCerts))
            return new AuthResult(false, null, [], StatusCodes.Status401Unauthorized,
                "client certificate does not chain to requestheader-client-ca");

        if (config.AllowedNames.Count > 0)
        {
            var cn = clientCert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!config.AllowedNames.Contains(cn))
                return new AuthResult(false, null, [], StatusCodes.Status401Unauthorized,
                    $"client certificate CN '{cn}' is not in requestheader-allowed-names");
        }

        var user = FirstHeader(ctx, config.UserHeaders);
        if (string.IsNullOrEmpty(user))
            return new AuthResult(false, null, [], StatusCodes.Status401Unauthorized, "missing remote-user header");

        var groups = config.GroupHeaders
            .SelectMany(h => ctx.Request.Headers[h].Where(v => v is not null).Cast<string>())
            .Distinct().ToList();

        return new AuthResult(true, user, groups, StatusCodes.Status200OK, null);
    }

    private static string? FirstHeader(HttpContext ctx, List<string> headers) =>
        headers.Select(h => ctx.Request.Headers[h].FirstOrDefault()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    // internal for unit testing — this chain decision is the entire trust boundary of the
    // aggregated API (TLS-layer client-cert validation is intentionally disabled; see Program.cs).
    internal static bool ChainsToCa(X509Certificate2 cert, X509Certificate2Collection caCerts)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(caCerts);
        chain.ChainPolicy.ExtraStore.AddRange(caCerts);
        return chain.Build(cert);
    }

    private async Task<Snapshot?> GetConfigAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _loadedAt < CacheTtl)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _loadedAt < CacheTtl)
                return _cached;

            var snapshot = await LoadAsync(ct);
            if (snapshot is not null)
            {
                _cached = snapshot;
                _loadedAt = DateTimeOffset.UtcNow;
            }
            return _cached; // keep last-good on a transient read failure
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Snapshot?> LoadAsync(CancellationToken ct)
    {
        try
        {
            var cm = await client.CoreV1.ReadNamespacedConfigMapAsync("extension-apiserver-authentication", "kube-system", cancellationToken: ct);
            var data = cm.Data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(cm.Data);

            if (!data.TryGetValue("requestheader-client-ca-file", out var caPem) || string.IsNullOrWhiteSpace(caPem))
            {
                logger.LogWarning("view: extension-apiserver-authentication has no requestheader-client-ca-file");
                return null;
            }

            var caCerts = new X509Certificate2Collection();
            caCerts.ImportFromPem(caPem);
            if (caCerts.Count == 0)
            {
                logger.LogWarning("view: requestheader-client-ca-file contained no certificates");
                return null;
            }

            return new Snapshot(
                caCerts,
                ParseJsonStringArray(data.GetValueOrDefault("requestheader-allowed-names")),
                Defaulted(ParseJsonStringArray(data.GetValueOrDefault("requestheader-username-headers")), "X-Remote-User"),
                Defaulted(ParseJsonStringArray(data.GetValueOrDefault("requestheader-group-headers")), "X-Remote-Group"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "view: failed to read extension-apiserver-authentication ConfigMap");
            return null;
        }
    }

    private static List<string> Defaulted(List<string> values, string fallback) =>
        values.Count > 0 ? values : [fallback];

    private static List<string> ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record Snapshot(
        X509Certificate2Collection CaCerts,
        List<string> AllowedNames,
        List<string> UserHeaders,
        List<string> GroupHeaders);
}
