using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Kubernetes;

public class TargetResolver(
    ILogger<TargetResolver> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    MsSqlOperator.Services.Health.IAgStateStore? stateStore = null) : ITargetResolver
{
    public async Task<List<ResolvedTarget>> ResolveAsync(TargetSelector selector, string crNamespace, CancellationToken ct = default)
    {
        // Resolution never leaves the CR's namespace.
        List<string> namespaces = [crNamespace];
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
                    catch (Exception ex) when (!ct.IsCancellationRequested)
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
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not resolve AG {Name} in {Namespace}", name, ns);
                    }
                }
            }
        }

        return resolved;
    }

    /// <summary>
    /// Opens a connection to the AG's primary replica.
    ///
    /// It used to return the FIRST replica that claimed PRIMARY. During a split-brain
    /// (two replicas claiming the role), that is a coin toss — and every consumer of this method runs
    /// DDL: database users, permissions, stored procedures, agent jobs, TDE certificates. Applying
    /// them to a stale primary meant the real one never received them, while the CRs happily reported
    /// Ready. All replicas are probed, and an ambiguous answer is refused rather than guessed.
    /// </summary>
    public async Task<(string ReplicaName, SqlConnection Connection)?> FindPrimaryReplicaAsync(
        string agName, string agNamespace, string saPassword, int replicaCount, CancellationToken ct = default)
    {
        // While the AG controller does not trust the replica answering PRIMARY (a split-brain, or the
        // previous primary back as PRIMARY while the survivor does not answer), the scan below found
        // it alone and ran DDL there — on the wrong timeline.
        if (await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, agNamespace, ct) is { } ag
            && ag.Status?.Conditions?.FirstOrDefault(c => c.Type == AgTopology.SplitBrainSafeCondition) is { Status: "False" })
        {
            logger.LogWarning("AG {Ag}: no trusted primary (SplitBrainSafe=False) — no DDL is run against it", agName);
            return null;
        }

        // The primary of the last health read, when fresh, is tried first — one connection
        // instead of one per replica — and confirmed on the server itself.
        if (stateStore?.FreshPrimary(agNamespace, agName) is { } hinted)
        {
            // The probe profile (3 s, no retry): a hint is at most one health read old, and the
            // primary it names may have died since — the regular profile then held the worker of
            // every kind that targets the AG for about 2.6 minutes before the scan.
            SqlConnection? hintedConnection = null;
            try
            {
                hintedConnection = await connectionFactory.CreateProbeConnectionAsync($"{hinted}.{agName}-headless.{agNamespace}.svc.cluster.local",
                    SqlEndpoints.ContainerPort, saPassword, "master", ct);
                if (await MsSqlOperator.Services.SqlServer.SqlProbes.IsLocalPrimaryAsync(hintedConnection, ct))
                    return (hinted, hintedConnection);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Hinted primary {Replica} of AG {Ag} could not confirm its role; scanning", hinted, agName);
            }
            if (hintedConnection is not null)
                await hintedConnection.DisposeAsync();
        }

        // Discovery uses the probe profile, every replica at once, under one deadline. The
        // regular profile retried 30 s × 5 per unreachable replica, one replica after the other: during
        // a node outage it held the workers of every kind that targets an AG (users, jobs, TDE,
        // procedures, the view API) for minutes.
        List<(string ReplicaName, string Host)> primaries;
        using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            discovery.CancelAfter(PrimaryDiscoveryTimeout);
            var claims = await Task.WhenAll(Enumerable.Range(0, replicaCount)
                .Select(i => ClaimsPrimaryAsync(agName, agNamespace, i, saPassword, discovery.Token, ct)));
            primaries = claims.OfType<(string, string)>().ToList();
        }

        if (primaries.Count > 1)
        {
            logger.LogError(
                "AG {Ag}: {Count} replicas report PRIMARY ({Replicas}) — refusing to run DDL against an arbitrary one. " +
                "Resolve the split-brain first (see the SplitBrainDetected event on the availability group).",
                agName, primaries.Count, string.Join(", ", primaries.Select(p => p.ReplicaName)));
            return null;
        }

        if (primaries.Count == 0)
            return null;

        // The callers run DDL: a regular connection (transient-error retries, normal timeouts), on
        // which the role is checked again — a failover may have happened since the probe.
        var (replicaName, host) = primaries[0];
        var connection = await connectionFactory.CreateConnectionAsync(host, SqlEndpoints.ContainerPort, saPassword, "master", ct);
        try
        {
            if (await MsSqlOperator.Services.SqlServer.SqlProbes.IsLocalPrimaryAsync(connection, ct))
                return (replicaName, connection);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Replica {Replica} could not confirm its primary role", replicaName);
        }

        await connection.DisposeAsync();
        return null;
    }

    private static readonly TimeSpan PrimaryDiscoveryTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The replica when it answers as the local PRIMARY, else null (unreachable included).</summary>
    private async Task<(string ReplicaName, string Host)?> ClaimsPrimaryAsync(
        string agName, string agNamespace, int ordinal, string saPassword, CancellationToken discovery, CancellationToken ct)
    {
        var replicaName = $"{agName}-{ordinal}";
        var host = SqlQuoting.BuildAgPodFqdn(agName, ordinal, agNamespace);
        try
        {
            await using var conn = await connectionFactory.CreateProbeConnectionAsync(host, SqlEndpoints.ContainerPort, saPassword, "master", discovery);
            return await MsSqlOperator.Services.SqlServer.SqlProbes.IsLocalPrimaryAsync(conn, discovery) ? (replicaName, host) : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Replica {Replica} not reachable or not primary", replicaName);
            return null;
        }
    }

    private async Task<ResolvedTarget?> ResolveInstanceAsync(SqlInstance instance, string ns, CancellationToken ct)
    {
        if (instance.Spec.SaPasswordSecretRef == null) return null;
        var pwd = await ResolveSecretValueAsync(instance.Spec.SaPasswordSecretRef, ns, ct);
        if (pwd == null) return null;

        var host = $"{instance.Metadata.Name}.{ns}.svc.cluster.local";
        return new ResolvedTarget(TargetKinds.Instance, instance.Metadata.Name, ns, host, instance.Spec.Service?.Port ?? 1433, pwd, null);
    }

    private async Task<ResolvedTarget?> ResolveAgAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        if (ag.Spec.SaPasswordSecretRef == null) return null;
        var pwd = await ResolveSecretValueAsync(ag.Spec.SaPasswordSecretRef, ns, ct);
        if (pwd == null) return null;

        // An availability group creates its Database Master Key with the dedicated
        // masterKeyPasswordSecretRef (mandatory at admission). Consumers — the TDE controller in
        // particular — must open it with THAT password, not with the SA one.
        var masterKeyPwd = ag.Spec.MasterKeyPasswordSecretRef is { } mkRef
            ? await ResolveSecretValueAsync(mkRef, ns, ct)
            : null;

        // "{ag}.{ns}.svc" named no Service at all. An AG is reached through its primary
        // (FindPrimaryReplicaAsync); Host is the R/W listener when the AG has one, and empty otherwise.
        var host = ag.Spec.Listener is { } listener ? $"{ag.Metadata.Name}-listener.{ns}.svc.cluster.local" : string.Empty;
        var port = ag.Spec.Listener?.Port ?? SqlEndpoints.ContainerPort;
        return new ResolvedTarget(TargetKinds.AvailabilityGroup, ag.Metadata.Name, ns, host, port, pwd,
            ag.Spec.Replicas.Count, masterKeyPwd);
    }

    private Task<string?> ResolveSecretValueAsync(SecretReference secretRef, string ns, CancellationToken ct)
        // Null only on 404; transport errors propagate (runtime backoff).
        => SecretValues.TryGetAsync(kubernetesClient, secretRef, ns, ct);
}
