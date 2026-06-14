using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ITargetResolver"/>: serves a configurable resolved-target
/// list and an optional primary-replica result.</summary>
public sealed class FakeTargetResolver : ITargetResolver
{
    public List<ResolvedTarget> Resolved { get; set; } = [];
    public (string ReplicaName, SqlConnection Connection)? Primary { get; set; }

    public Task<List<ResolvedTarget>> ResolveAsync(TargetSelector selector, string crNamespace, CancellationToken ct = default)
        => Task.FromResult(Resolved);

    public Task<(string ReplicaName, SqlConnection Connection)?> FindPrimaryReplicaAsync(
        string agName, string agNamespace, string saPassword, int replicaCount, CancellationToken ct = default)
        => Task.FromResult(Primary);
}
