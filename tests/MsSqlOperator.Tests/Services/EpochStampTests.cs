using k8s.Models;
using Microsoft.Data.SqlClient;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Services.Control;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>Topology commands under the applock and epoch stamp of their AG.</summary>
public class EpochStampTests
{
    private sealed class FakeStamper : IEpochStamper
    {
        public long Stamp { get; set; }
        public List<(string Scope, long Epoch, bool Enforce)> Calls { get; } = [];

        public async Task<long> RunAsync(SqlConnection connection, string scope, long epoch, string leader, bool enforce,
            Func<CancellationToken, Task> command, CancellationToken ct)
        {
            Calls.Add((scope, epoch, enforce));
            if (enforce && Stamp > epoch)
                throw new StaleEpochException(scope, epoch, Stamp);
            await command(ct);
            var before = Stamp;
            Stamp = Math.Max(Stamp, epoch);
            return before;
        }
    }

    private sealed class Epoch : IOperatorEpoch
    {
        public long Current { get; set; } = 5;
        public Task<long> AdvanceAsync(CancellationToken ct) => Task.FromResult(++Current);
        public Task<long> RaiseAboveAsync(long seen, CancellationToken ct) => Task.FromResult(Current = Math.Max(Current, seen) + 1);
    }

    private readonly FakeStamper _stamper = new();
    private readonly Epoch _epoch = new();
    private static readonly SqlAvailabilityGroup Ag = new() { Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "db" } };

    private ActuationGateway Gateway(SafetyMode? mode)
        => new(new ActuationPolicy(new AutomationOptions(), TimeProvider.System), new PlanStore(),
            stamper: _stamper, epoch: _epoch, epochMode: _ => mode);

    [Fact]
    public async Task Stamped_RunsTheCommandUnderTheEpoch()
    {
        var ran = false;
        await Gateway(SafetyMode.Shadow).Stamped(Ag, new SqlConnection(), "sales", _ => { ran = true; return Task.CompletedTask; })(CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(("sales", 5L, false), Assert.Single(_stamper.Calls));
    }

    [Fact]
    public async Task Enforce_AStaleEpochIsRefused_AndTheEpochRaisedAboveTheStamp()
    {
        // A leader paused during an election comes back with an old epoch.
        _stamper.Stamp = 9;
        var ran = false;

        await Assert.ThrowsAsync<StaleEpochException>(() =>
            Gateway(SafetyMode.Enforce).Stamped(Ag, new SqlConnection(), "sales", _ => { ran = true; return Task.CompletedTask; })(CancellationToken.None));

        Assert.False(ran);
        Assert.Equal(10, _epoch.Current);
    }

    [Fact]
    public async Task Shadow_AStaleEpochRunsButRaisesTheEpoch()
    {
        _stamper.Stamp = 9;
        await Gateway(SafetyMode.Shadow).Stamped(Ag, new SqlConnection(), "sales", _ => Task.CompletedTask)(CancellationToken.None);

        Assert.Equal(10, _epoch.Current);
        Assert.Equal(9, _stamper.Stamp); // never lowered
    }

    [Fact]
    public async Task LevelNotRunning_NoStamp()
    {
        await Gateway(null).Stamped(Ag, new SqlConnection(), "sales", _ => Task.CompletedTask)(CancellationToken.None);
        Assert.Empty(_stamper.Calls);
    }

    [Theory]
    [InlineData(51001, MsSqlOperator.Services.ConnectionFactory.SqlErrorKind.ApplockNotAcquired)]
    [InlineData(51002, MsSqlOperator.Services.ConnectionFactory.SqlErrorKind.StaleEpoch)]
    public void Taxonomy_KnowsTheEpochErrors(int number, MsSqlOperator.Services.ConnectionFactory.SqlErrorKind kind)
        => Assert.Equal(kind, MsSqlOperator.Services.ConnectionFactory.SqlErrors.Classify(number));
}
