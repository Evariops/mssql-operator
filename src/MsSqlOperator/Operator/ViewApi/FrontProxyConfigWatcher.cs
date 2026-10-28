using k8s;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// The <c>extension-apiserver-authentication</c> ConfigMap is watched — a rotation of the
/// front-proxy CA or of the allowed names takes effect at once, instead of within the 10 min the
/// authenticator's TTL allowed. The TTL stays as a fallback; a lost watch relists (ResourceWatcher).
/// </summary>
public sealed class FrontProxyConfigWatcher(IKubernetes client, FrontProxyAuthenticator authenticator, ILogger<FrontProxyConfigWatcher> logger)
    : BackgroundService
{
    private const string Name = "extension-apiserver-authentication";
    private const string Namespace = "kube-system";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watcher = new ResourceWatcher("ConfigMap (front-proxy)", logger,
            async ct => KubernetesJson.Serialize(await client.CoreV1.ListNamespacedConfigMapAsync(Namespace,
                fieldSelector: $"metadata.name={Name}", cancellationToken: ct).ConfigureAwait(false)),
            (rv, onActivity, onEvent, ct) => K8sWatchSession.RunAsync(
                client.CoreV1.WatchListNamespacedConfigMapAsync(Namespace, fieldSelector: $"metadata.name={Name}",
                    allowWatchBookmarks: true, timeoutSeconds: 300, resourceVersion: rv, cancellationToken: ct),
                "ConfigMap (front-proxy)", logger, onActivity, onEvent, ct),
            _ => authenticator.Invalidate(),
            (_, _) =>
            {
                logger.LogInformation("Front-proxy configuration changed; reloading it on the next request");
                authenticator.Invalidate();
            },
            stopIfResourceAbsent: true);
        await watcher.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
