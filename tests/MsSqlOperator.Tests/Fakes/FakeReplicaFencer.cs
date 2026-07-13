using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IReplicaFencer"/>: records fencing
/// attempts and serves a configurable outcome. The default is "not confirmed" — the safe default,
/// and the one that forces tests to be explicit about a proven fencing.</summary>
public sealed class FakeReplicaFencer : IReplicaFencer
{
    public readonly List<string> FenceCalls = [];
    public FenceResult Result { get; set; } = FenceResult.No("fake: fencing not confirmed");

    public Task<FenceResult> FenceAsync(string podName, string ns, CancellationToken ct)
    {
        FenceCalls.Add(podName);
        return Task.FromResult(Result);
    }

    public static FenceResult Confirmed =>
        new(FenceMethod.VolumeDetached, "fake: all volume attachments detached");
}

/// <summary>Hand-written fake for <see cref="IFullTextManager"/>.</summary>
public sealed class FakeFullTextManager : IFullTextManager
{
    public FullTextStatusInfo Status { get; set; } = FullTextStatusInfo.NotInstalled;
    public readonly List<string> UpgradeOptionCalls = [];

    public Task<FullTextStatusInfo> GetStatusAsync(SqlConnection connection, CancellationToken ct = default)
        => Task.FromResult(Status);

    public Task SetUpgradeOptionAsync(SqlConnection connection, string option, CancellationToken ct = default)
    {
        UpgradeOptionCalls.Add(option);
        return Task.CompletedTask;
    }

    public static FullTextStatusInfo Populating(string database = "app") =>
        new(true, "Import", [new FullTextPopulationInfo(database, "FULL", "Active", null)]);
}
