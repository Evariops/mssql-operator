using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Shared connection routing for view providers. A <see cref="ResolvedTarget"/> is either a single
/// <c>SqlInstance</c> (connect directly) or a <c>SqlAvailabilityGroup</c> (connect to the live primary
/// replica, since the catalog/DMV truth the providers read lives on the primary). Centralises what the
/// controllers do inline so every view resource handles both target kinds identically.
/// </summary>
public static class ViewTargets
{
    /// <summary>
    /// Opens a master-database connection to <paramref name="target"/>. Returns <c>null</c> when an AG
    /// has no reachable primary (caller skips the target). The caller owns disposal of the connection.
    /// </summary>
    public static async Task<SqlConnection?> OpenAsync(
        ResolvedTarget target, ISqlConnectionFactory connectionFactory, ITargetResolver targetResolver, CancellationToken ct)
    {
        if (target.Kind == "SqlAvailabilityGroup")
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(
                target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
            return primary?.Connection;
        }

        return await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
    }
}
