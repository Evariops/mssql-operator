using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Connections to a <see cref="ResolvedTarget"/>, shared by the SqlUser controller and finalizer
/// (they each carried a copy, and only one of them disposed the connections already opened
/// when a later replica failed).
/// </summary>
public static class TargetConnections
{
    /// <summary>The target's writable endpoint: the live PRIMARY of an AG (never ordinal
    /// 0), the instance itself otherwise. Null when no primary is reachable.</summary>
    public static async Task<SqlConnection?> OpenPrimaryAsync(ResolvedTarget target, ISqlConnectionFactory connectionFactory,
        ITargetResolver targetResolver, CancellationToken ct)
    {
        if (target.IsAvailabilityGroup)
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(
                target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
            return primary?.Connection;
        }

        // Logins, users, jobs — DDL that must not queue behind an application lock.
        return await connectionFactory.OpenAsync(target.Host, target.Port, target.SaPassword, SqlProfile.Ddl, "master", ct);
    }

    /// <summary>Every server of the target, without connecting: each replica of an AG (logins and
    /// msdb are server-scoped, not replicated), or the instance. The one enumeration of an
    /// AG's replicas — the copies differed in where the count came from and in error handling.</summary>
    public static async Task<List<(string Host, int Port, string? ReplicaName)>> EveryServerAsync(ResolvedTarget target,
        IKubernetesClient kubernetesClient, CancellationToken ct)
    {
        if (!target.IsAvailabilityGroup)
            return [(target.Host, target.Port, null)];

        var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct);
        return Enumerable.Range(0, ag?.Spec?.Replicas.Count ?? 0)
            .Select(i => (SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace), SqlEndpoints.ContainerPort, (string?)$"{target.Name}-{i}"))
            .ToList();
    }

    /// <summary>
    /// One connection per server, opened in parallel with the probe profile (3 s, no retry)
    /// and given the DDL session settings — a dead replica costs seconds, not the retries of the
    /// regular profile, and does not stop the others: it is returned in <c>Failed</c>. The caller
    /// disposes the open ones.
    /// </summary>
    public static async Task<(List<(SqlConnection Connection, string? ReplicaName)> Open, List<(string Server, string Error)> Failed)>
        OpenEveryServerPartialAsync(ResolvedTarget target, ISqlConnectionFactory connectionFactory, IKubernetesClient kubernetesClient,
            CancellationToken ct)
    {
        var servers = await EveryServerAsync(target, kubernetesClient, ct);
        var attempts = await Task.WhenAll(servers.Select(async server =>
        {
            SqlConnection? conn = null;
            try
            {
                conn = await connectionFactory.OpenAsync(server.Host, server.Port, target.SaPassword, SqlProfile.Probe, "master", ct);
                await SqlSessions.ApplyAsync(conn, SqlProfileSettings.DdlSession, ct);
                return (Connection: conn, server.ReplicaName, Error: (string?)null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (conn is not null)
                    await conn.DisposeAsync();
                return (Connection: (SqlConnection?)null, server.ReplicaName, Error: ex.Message);
            }
        }));
        var open = attempts.Where(a => a.Connection is not null).Select(a => (a.Connection!, a.ReplicaName)).ToList();
        var failed = attempts.Where(a => a.Connection is null).Select(a => (a.ReplicaName ?? target.Name, a.Error!)).ToList();
        return (open, failed);
    }

    /// <summary>One connection per server (<see cref="EveryServerAsync"/>). The caller disposes them.</summary>
    public static async Task<List<(SqlConnection Connection, string? ReplicaName)>> OpenEveryServerAsync(ResolvedTarget target,
        ISqlConnectionFactory connectionFactory, IKubernetesClient kubernetesClient, CancellationToken ct)
    {
        var results = new List<(SqlConnection, string?)>();
        try
        {
            foreach (var (host, port, replicaName) in await EveryServerAsync(target, kubernetesClient, ct))
            {
                var conn = await connectionFactory.OpenAsync(host, port, target.SaPassword, SqlProfile.Ddl, "master", ct);
                results.Add((conn, replicaName));
            }
        }
        catch
        {
            // Failing on replica N must not leak the N-1 connections already opened.
            foreach (var (conn, _) in results)
                await conn.DisposeAsync();
            throw;
        }

        return results;
    }

    /// <summary>The annotations of the target's CR, read fresh — the opt-ins they carry
    /// may change at any time, the reconcile must see the current value. One implementation
    /// for the three controllers that need it.</summary>
    public static async Task<IDictionary<string, string>?> GetAnnotationsAsync(ResolvedTarget target,
        IKubernetesClient kubernetesClient, CancellationToken ct) => target.Kind switch
        {
            TargetKinds.Instance => (await kubernetesClient.GetAsync<SqlInstance>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            TargetKinds.AvailabilityGroup => (await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            _ => null,
        };
}
