using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public interface ITargetResolver
{
    Task<List<ResolvedTarget>> ResolveAsync(TargetSelector selector, string crNamespace, CancellationToken ct = default);
    Task<(string ReplicaName, SqlConnection Connection)?> FindPrimaryReplicaAsync(string agName, string agNamespace, string saPassword, int replicaCount, CancellationToken ct = default);
}
