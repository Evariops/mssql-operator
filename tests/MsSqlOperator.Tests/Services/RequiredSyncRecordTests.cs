using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// status.lastRequiredSynchronizedSecondaries is what a failover without the primary reads; it was
/// written after the setting was lowered, at the end of the pass — a failed status write left it at
/// 1 while the primary committed alone.
/// </summary>
public class RequiredSyncRecordTests
{
    private readonly FakeKubernetesClient _k8s = new();

    private static SqlAvailabilityGroup Ag(int? recorded) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default" },
        Status = new SqlAvailabilityGroupStatus { LastRequiredSynchronizedSecondaries = recorded },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task Lowering_IsRecordedFirst(int? recorded)
    {
        var ag = Ag(recorded);

        await RequiredSyncRecord.BeforeLoweringAsync(_k8s, ag, 0, CancellationToken.None);

        var patch = Assert.Single(_k8s.PatchStatusCalls);
        Assert.Equal(0, patch.Patch["lastRequiredSynchronizedSecondaries"]);
        Assert.Equal(0, ag.Status!.LastRequiredSynchronizedSecondaries);
    }

    [Fact]
    public async Task AlreadyRecordedAsLowOrLower_NothingWritten()
    {
        await RequiredSyncRecord.BeforeLoweringAsync(_k8s, Ag(0), 0, CancellationToken.None);

        Assert.Empty(_k8s.PatchStatusCalls);
    }

    [Fact]
    public async Task RecordFails_TheSettingMustNotBeLowered()
    {
        _k8s.PatchStatusFailure = new InvalidOperationException("409 Conflict");
        var ag = Ag(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RequiredSyncRecord.BeforeLoweringAsync(_k8s, ag, 0, CancellationToken.None));
        Assert.Equal(1, ag.Status!.LastRequiredSynchronizedSecondaries);
    }
}
