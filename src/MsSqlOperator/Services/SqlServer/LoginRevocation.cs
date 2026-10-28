using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>Outcome of a revocation on every server of a target: the servers handled
/// (<c>{kind}/{target}[/{replica}]</c>), and those that could not be reached or failed, with why.</summary>
public sealed record RevocationOutcome(List<string> Done, List<(string Server, string Error)> Pending);

/// <summary>
/// Revokes a login on every server of a target, each one independently. A stopped replica
/// no longer holds back the revocation on the others: the login is disabled at once wherever the
/// server answers, and the stopped one is reported pending until it comes back. On each server the
/// login is disabled first — even when it is to be dropped — then its sessions are killed, then
/// (for a drop) its database users and the login itself are dropped.
/// </summary>
public static class LoginRevocation
{
    /// <remarks>Every step goes through <paramref name="gateway"/>, on behalf of
    /// <paramref name="owner"/>: the revocation declared by the spec, or the cleanup of its deletion.</remarks>
    public static async Task<RevocationOutcome> RevokeAsync(ResolvedTarget target, string login, bool drop, bool terminateSessions,
        ISqlConnectionFactory connectionFactory, IKubernetesClient kubernetesClient, ISqlUserManager users,
        ISqlServerManager servers, ILogger logger, CancellationToken ct,
        Actuation.ActuationGateway? gateway = null, k8s.IKubernetesObject<k8s.Models.V1ObjectMeta>? owner = null,
        Actuation.ActuationIntent intent = Actuation.ActuationIntent.SpecDeclared)
    {
        gateway ??= Actuation.ActuationGateway.Default;
        owner ??= new k8s.Models.V1Secret { Metadata = new k8s.Models.V1ObjectMeta { Name = login, NamespaceProperty = target.Namespace } };
        var outcome = new RevocationOutcome([], []);
        foreach (var (host, port, replica) in await TargetConnections.EveryServerAsync(target, kubernetesClient, ct))
        {
            var server = replica is null ? $"{target.Kind}/{target.Name}" : $"{target.Kind}/{target.Name}/{replica}";
            try
            {
                await using var connection = await connectionFactory.CreateProbeConnectionAsync(host, port, target.SaPassword, "master", ct);
                if (await users.LoginExistsAsync(connection, login, ct))
                {
                    await gateway.DisableLoginAsync(owner, users, connection, login, server, intent, ct);
                    if (terminateSessions || drop)
                        await gateway.KillSessionsAsync(owner, users, connection, login, server, intent, ct);
                }
                if (drop)
                {
                    // Database users only in the writable databases of this server: an AG database is
                    // dropped from on its primary, and replication carries the drop to the secondaries.
                    foreach (var db in await servers.GetDatabaseNamesAsync(connection, ct))
                    {
                        if (!await users.DatabaseUserExistsAsync(connection, db, login, ct))
                            continue;
                        try
                        {
                            await gateway.DropDatabaseUserAsync(owner, users, connection, db, login, server, intent, ct);
                        }
                        catch (Microsoft.Data.SqlClient.SqlException)
                        {
                            await users.TransferSchemaOwnershipAsync(connection, db, login, ct);
                            await gateway.DropDatabaseUserAsync(owner, users, connection, db, login, server, intent, ct);
                        }
                    }
                    if (await users.LoginExistsAsync(connection, login, ct))
                        await gateway.DropLoginAsync(owner, users, connection, login, server, intent, ct);
                }
                outcome.Done.Add(server);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Revocation of login {Login} pending on {Server}", login, server);
                outcome.Pending.Add((server, ex.Message));
            }
        }
        return outcome;
    }
}
