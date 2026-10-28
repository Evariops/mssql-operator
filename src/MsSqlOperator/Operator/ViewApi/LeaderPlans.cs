using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Actuation;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>A plan as the leader hands it to a follower.</summary>
public sealed record RemotePlan(string Key, List<PlannedAction> Actions);

/// <summary>The plans live on the leader, and it cannot be reached: said, never served as an empty plan.</summary>
public sealed class ViewUnavailableException(string message) : Exception(message);

/// <summary>
/// The plans live in the memory of the leader, and the APIService sends each
/// request to either pod: <c>kubectl get operatorplans</c> came back empty whenever the follower
/// answered — in Plan mode too, where the plan is all there is to see. A follower asks the leader,
/// on the serving port; each side proves itself with the serving certificate both pods mount.
/// During a renewal the two pods may hold different certificates for a few seconds: the follower
/// answers 503 meanwhile.
/// </summary>
public sealed class LeaderPlans(IKubernetesClient client, OperatorOptions options, Func<X509Certificate2> certificate, int port,
    Func<HttpMessageHandler>? handler = null)
{
    public const string Path = "/internal/v1alpha1/operatorplans";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The plans of the leader, or null when this pod is the leader.</summary>
    public async Task<IReadOnlyList<(string Key, IReadOnlyList<PlannedAction> Actions)>?> FetchAsync(CancellationToken ct)
    {
        if (!options.LeaderElection)
            return null;
        var ns = options.ResolveNamespace();
        var holder = (await client.GetAsync<V1Lease>(options.LeaseName, ns, ct))?.Spec?.HolderIdentity;
        if (string.IsNullOrEmpty(holder))
            throw new ViewUnavailableException("No operator replica leads right now, and the plans live on the leader: retry shortly.");
        if (holder == options.Identity)
            return null;
        if ((await client.GetAsync<V1Pod>(holder, ns, ct))?.Status?.PodIP is not { Length: > 0 } ip)
            throw new ViewUnavailableException($"The plans live on the leader {holder}, which has no address: retry shortly.");

        using var http = new HttpClient(handler?.Invoke() ?? PinnedHandler(), disposeHandler: true) { Timeout = Timeout };
        try
        {
            var body = await http.GetStringAsync($"https://{(ip.Contains(':') ? $"[{ip}]" : ip)}:{port}{Path}", ct);
            return (JsonSerializer.Deserialize<List<RemotePlan>>(body, ViewApiJson.Options) ?? [])
                .Select(p => (p.Key, (IReadOnlyList<PlannedAction>)p.Actions)).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new ViewUnavailableException($"The plans live on the leader {holder}, which did not answer ({e.Message}): retry shortly.");
        }
    }

    /// <summary>Both pods serve the certificate of the same Secret: the peer must present it too.</summary>
    public static bool SameCertificate(X509Certificate? presented, X509Certificate2 own)
        => presented is not null && presented.GetCertHash().AsSpan().SequenceEqual(own.GetCertHash());

    private SocketsHttpHandler PinnedHandler() => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = [certificate()],
            // The leader is reached by its pod address, which no certificate names: it is pinned instead.
            RemoteCertificateValidationCallback = (_, presented, _, _) => SameCertificate(presented, certificate()),
        },
    };
}
