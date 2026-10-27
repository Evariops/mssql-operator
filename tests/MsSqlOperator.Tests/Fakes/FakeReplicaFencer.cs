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

    /// <summary>Results served in order (one per call) before falling back to <see cref="Result"/> —
    /// models a fencing that completes over several passes.</summary>
    public Queue<FenceResult> Sequence { get; } = new();

    /// <summary>Thrown by the next calls, before the state is touched — an API-server error on the
    /// first GET of the pod.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Stage recorded with a pending result, as the real fencer does once it has asked for
    /// the deletion (a graceful deletion is followed within the pass).</summary>
    public string? PendingStage { get; set; }

    public Task<FenceResult> FenceAsync(string podName, string ns, MsSqlOperator.Entities.V1Alpha1.AgFencingStatus state,
        TimeSpan storageTimeout, CancellationToken ct)
    {
        FenceCalls.Add(podName);
        if (Throws is not null)
            throw Throws;
        state.Pod = podName;
        var result = Sequence.Count > 0 ? Sequence.Dequeue() : Result;
        if (result.InProgress && PendingStage is not null)
            state.Stage = PendingStage;
        return Task.FromResult(result);
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
