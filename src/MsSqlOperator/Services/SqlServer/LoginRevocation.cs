using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
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
    public static async Task<RevocationOutcome> RevokeAsync(ResolvedTarget target, string login, bool drop, bool terminateSessions,
        ISqlConnectionFactory connectionFactory, IKubernetesClient kubernetesClient, ISqlUserManager users,
        ISqlServerManager servers, ILogger logger, CancellationToken ct)
    {
        var outcome = new RevocationOutcome([], []);
        foreach (var (host, port, replica) in await TargetConnections.EveryServerAsync(target, kubernetesClient, ct))
        {
            var server = replica is null ? $"{target.Kind}/{target.Name}" : $"{target.Kind}/{target.Name}/{replica}";
            try
            {
                await using var connection = await connectionFactory.CreateProbeConnectionAsync(host, port, target.SaPassword, "master", ct);
                if (await users.LoginExistsAsync(connection, login, ct))
                {
                    await users.DisableLoginAsync(connection, login, ct);
                    if (terminateSessions || drop)
                        await users.KillSessionsAsync(connection, login, ct);
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
                            await users.DropDatabaseUserAsync(connection, db, login, ct);
                        }
                        catch (Microsoft.Data.SqlClient.SqlException)
                        {
                            await users.TransferSchemaOwnershipAsync(connection, db, login, ct);
                            await users.DropDatabaseUserAsync(connection, db, login, ct);
                        }
                    }
                    if (await users.LoginExistsAsync(connection, login, ct))
                        await users.DropLoginAsync(connection, login, ct);
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
