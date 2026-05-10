using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.Services.Kubernetes;

public class TargetResolver(
    ILogger<TargetResolver> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory) : ITargetResolver
{
    public async Task<List<ResolvedTarget>> ResolveAsync(TargetSelector selector, string crNamespace, CancellationToken ct = default)
    {
        var namespaces = selector.Namespaces is { Count: > 0 } ? selector.Namespaces : [crNamespace];
        var seen = new HashSet<string>();
        var resolved = new List<ResolvedTarget>();

        // Match by labels
        if (selector.MatchLabels is { Count: > 0 })
        {
            var labelSelector = string.Join(",", selector.MatchLabels.Select(kv => $"{kv.Key}={kv.Value}"));
            foreach (var ns in namespaces)
            {
                var instances = await kubernetesClient.ListAsync<SqlInstance>(ns, labelSelector, ct);
                foreach (var instance in instances)
                {
                    var key = $"SqlInstance/{ns}/{instance.Metadata.Name}";
                    if (!seen.Add(key)) continue;

                    var target = await ResolveInstanceAsync(instance, ns, ct);
                    if (target != null) resolved.Add(target);
                }

                var ags = await kubernetesClient.ListAsync<SqlAvailabilityGroup>(ns, labelSelector, ct);
                foreach (var ag in ags)
                {
                    var key = $"SqlAvailabilityGroup/{ns}/{ag.Metadata.Name}";
                    if (!seen.Add(key)) continue;

                    var target = await ResolveAgAsync(ag, ns, ct);
                    if (target != null) resolved.Add(target);
                }
            }
        }

        // Explicit instances
        if (selector.Instances != null)
        {
            foreach (var ns in namespaces)
            {
                foreach (var name in selector.Instances)
                {
                    var key = $"SqlInstance/{ns}/{name}";
                    if (!seen.Add(key)) continue;

                    try
                    {
                        var instance = await kubernetesClient.GetAsync<SqlInstance>(name, ns, ct);
                        if (instance != null)
                        {
                            var target = await ResolveInstanceAsync(instance, ns, ct);
                            if (target != null) resolved.Add(target);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not resolve instance {Name} in {Namespace}", name, ns);
                    }
                }
            }
        }

        // Explicit availability groups
        if (selector.AvailabilityGroups != null)
        {
            foreach (var ns in namespaces)
            {
                foreach (var name in selector.AvailabilityGroups)
                {
                    var key = $"SqlAvailabilityGroup/{ns}/{name}";
                    if (!seen.Add(key)) continue;

                    try
                    {
                        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(name, ns, ct);
                        if (ag != null)
                        {
                            var target = await ResolveAgAsync(ag, ns, ct);
                            if (target != null) resolved.Add(target);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not resolve AG {Name} in {Namespace}", name, ns);
                    }
                }
            }
        }

        return resolved;
    }

    public async Task<(string ReplicaName, SqlConnection Connection)?> FindPrimaryReplicaAsync(
        string agName, string agNamespace, string saPassword, int replicaCount, CancellationToken ct = default)
    {
        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{agName}-{i}";
            var host = $"{replicaName}.{agNamespace}.svc.cluster.local";
            try
            {
                var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM sys.dm_hadr_availability_replica_states WHERE role_desc = 'PRIMARY' AND is_local = 1";
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result != null)
                    return (replicaName, conn);

                await conn.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Replica {Replica} not reachable or not primary", replicaName);
            }
        }

        return null;
    }

    private async Task<ResolvedTarget?> ResolveInstanceAsync(SqlInstance instance, string ns, CancellationToken ct)
    {
        if (instance.Spec.SaPasswordSecretRef == null) return null;
        var pwd = await ResolveSecretValueAsync(instance.Spec.SaPasswordSecretRef, ns, ct);
        if (pwd == null) return null;

        var host = $"{instance.Metadata.Name}.{ns}.svc.cluster.local";
        return new ResolvedTarget("SqlInstance", instance.Metadata.Name, ns, host, instance.Spec.Service?.Port ?? 1433, pwd, null);
    }

    private async Task<ResolvedTarget?> ResolveAgAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        if (ag.Spec.SaPasswordSecretRef == null) return null;
        var pwd = await ResolveSecretValueAsync(ag.Spec.SaPasswordSecretRef, ns, ct);
        if (pwd == null) return null;

        var host = $"{ag.Metadata.Name}.{ns}.svc.cluster.local";
        return new ResolvedTarget("SqlAvailabilityGroup", ag.Metadata.Name, ns, host, 1433, pwd, ag.Spec.Replicas.Count);
    }

    private async Task<string?> ResolveSecretValueAsync(SecretReference secretRef, string ns, CancellationToken ct)
    {
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(secretRef.Key, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }
}
