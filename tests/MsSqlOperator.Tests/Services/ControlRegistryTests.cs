using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Control;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The control registry, the operator epoch and their Shadow integration.</summary>
public class ControlRegistryTests
{
    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FixedEpoch _epoch = new() { Current = 42 };
    private readonly ControlRegistry _registry;

    public ControlRegistryTests() => _registry = new ControlRegistry(_k8s, _epoch);

    private sealed class FixedEpoch : IOperatorEpoch
    {
        public long Current { get; set; }
        public Task<long> AdvanceAsync(CancellationToken ct) => Task.FromResult(++Current);
        public Task<long> RaiseAboveAsync(long seen, CancellationToken ct) => Task.FromResult(Current = Math.Max(Current, seen) + 1);
    }

    private static SqlAvailabilityGroup Ag(string? level = null) => new()
    {
        ApiVersion = "mssql-operator.evariops.com/v1alpha1",
        Kind = "SqlAvailabilityGroup",
        Metadata = new V1ObjectMeta
        {
            Name = "sales",
            NamespaceProperty = "db",
            Uid = "uid",
            Annotations = level is null ? null : new Dictionary<string, string> { [MsSqlOperator.Core.Safety.SafetyLevel.Annotation] = level },
        },
        Spec = new SqlAvailabilityGroupSpec(),
        Status = new SqlAvailabilityGroupStatus(),
    };

    [Fact]
    public async Task Begin_ThenAnotherIntent_IsRefusedUntilTheFirstCompletes()
    {
        var record = await _registry.EnsureAsync(Ag(), "ag-sales", null, CancellationToken.None);

        await _registry.BeginAsync(record, new ControlIntent { Id = "fo-1", Type = "Failover", Stage = "Planned" }, CancellationToken.None);
        await Assert.ThrowsAsync<IntentConflictException>(() =>
            _registry.BeginAsync(record, new ControlIntent { Id = "fo-2", Type = "Failover", Stage = "Planned" }, CancellationToken.None));

        await _registry.CompleteAsync(record, "fo-1", null, CancellationToken.None);
        await _registry.BeginAsync(record, new ControlIntent { Id = "fo-2", Type = "Failover", Stage = "Planned" }, CancellationToken.None);
        Assert.Equal("fo-2", record.Spec.Intent!.Id);
    }

    [Fact]
    public async Task EveryWrite_IsACompareAndSwapCarryingTheEpoch()
    {
        var record = await _registry.EnsureAsync(Ag(), "ag-sales", null, CancellationToken.None);
        record.Metadata.ResourceVersion = "7";

        await _registry.BeginAsync(record, new ControlIntent { Id = "fo-1", Type = "Failover", Stage = "Planned" }, CancellationToken.None);

        var (_, ops) = Assert.Single(_k8s.PatchCalls);
        Assert.Equal("/spec", ops.Single()["path"]); // the resourceVersion test is added by PatchAsync itself
        Assert.Equal(42, record.Spec.Epoch);
        Assert.Equal(42, record.Spec.Intent!.Epoch);
    }

    [Fact]
    public async Task Ensure_MissingRecord_IsReconstructedFromWhatIsKnown()
    {
        var ag = Ag();
        ag.Status!.MachineKeySha256 = new string('a', 64);
        ag.Status.OpenDivergences = [new AgDivergenceStatus { Id = "dv-1", From = "sales-0", To = "sales-1" }];

        var record = await _registry.EnsureAsync(ag, "ag-sales", spec => ControlMirror.Reconstruct(ag, spec), CancellationToken.None);

        Assert.NotNull(record.Spec.ReconstructedAt);
        Assert.Equal("dv-1", Assert.Single(record.Spec.OpenDivergences!).Id);
        Assert.Equal(ag.Status.MachineKeySha256, record.Spec.Keys!.MachineKeySha256);
        Assert.Null(record.Metadata.OwnerReferences); // it outlives the AG
    }

    [Fact]
    public void Bound_KeepsTheNewestEntries()
    {
        var spec = new SqlControlRecordSpec
        {
            OpenDivergences = Enumerable.Range(0, 20).Select(i => new ControlDivergence { Id = $"dv-{i}", At = DateTime.UnixEpoch.AddDays(i) }).ToList(),
        };

        ControlRegistry.Bound(spec);

        Assert.Equal(16, spec.OpenDivergences!.Count);
        Assert.Equal("dv-4", spec.OpenDivergences[0].Id);
    }

    [Fact]
    public async Task OperatorEpoch_IsAboveTheLeaseCounterAndEveryRecord()
    {
        _k8s.SetList<SqlControlRecord>([new SqlControlRecord { Metadata = new V1ObjectMeta { Name = "ag-a" }, Spec = new SqlControlRecordSpec { Epoch = 9 } }]);
        var epoch = new OperatorEpoch(_k8s, "ops");

        Assert.Equal(10, await epoch.AdvanceAsync(CancellationToken.None));
        Assert.Equal(17, await epoch.RaiseAboveAsync(16, CancellationToken.None)); // A SQL stamp at 16
        Assert.Equal(17, epoch.Current);
    }

    [Theory]
    [InlineData(null, false)] // level 0, level 1 in Shadow: a registry failure is only logged
    [InlineData("1", true)]   // level 1 in Enforce: no effect without its intent
    public async Task Mirror_RegistryFailure_StopsTheOperationOnlyInEnforce(string? level, bool throws)
    {
        var mirror = new ControlMirror(_registry, new OperatorOptions { SafetyLevelMax = 1 }, NullLogger<ControlMirror>.Instance);
        _k8s.SetGetFailure<SqlControlRecord>("ag-sales", "db", new HttpRequestException("API down"));
        var failover = new AgLastFailoverStatus { Timestamp = DateTime.UnixEpoch, From = "sales-0", To = "sales-1" };

        var run = mirror.BeginAsync(Ag(level), failover, CancellationToken.None);

        if (throws) await Assert.ThrowsAsync<HttpRequestException>(() => run);
        else await run;
    }

    [Fact]
    public async Task Mirror_DataLossFailover_RecordsItsDivergenceInTheRegistry()
    {
        var mirror = new ControlMirror(_registry, new OperatorOptions { SafetyLevelMax = 1 }, NullLogger<ControlMirror>.Instance);
        var failover = new AgLastFailoverStatus { Timestamp = new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc), From = "sales-0", To = "sales-1", DataLoss = true };

        await mirror.BeginAsync(Ag(), failover, CancellationToken.None);
        await mirror.CompleteAsync(Ag(), failover, CancellationToken.None);

        var record = (await _registry.GetAsync("ag-sales", "db", CancellationToken.None))!;
        Assert.Null(record.Spec.Intent);
        Assert.Equal("dv-20261028080000-sales-1", Assert.Single(record.Spec.OpenDivergences!).Id);
    }

    [Theory]
    [InlineData(true, false, true)]   // the AG exists
    [InlineData(false, true, true)]   // its volumes remain
    [InlineData(false, false, false)] // both gone: the record goes
    public async Task Sweeper_KeepsTheRecordWhileTheResourceOrItsVolumesExist(bool agExists, bool pvcExists, bool kept)
    {
        if (agExists) _k8s.SetGet("sales", "db", Ag());
        _k8s.SetList<V1PersistentVolumeClaim>(pvcExists ? [new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "data-sales-0" } }] : []);
        var sweeper = new ControlRecordSweeper(_registry, _k8s, NullLogger<ControlRecordSweeper>.Instance);
        var record = new SqlControlRecord
        {
            Metadata = new V1ObjectMeta { Name = "ag-sales", NamespaceProperty = "db" },
            Spec = new SqlControlRecordSpec { Owner = new ControlOwner { Kind = "SqlAvailabilityGroup", Name = "sales" } },
        };

        Assert.Equal(kept, await sweeper.StillNeededAsync(record, CancellationToken.None));
    }
}
