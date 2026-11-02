using k8s.LeaderElection;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Operator.Registration;

namespace MsSqlOperator.Tests.Operator;

/// <summary>A graceful stop hands the Lease over instead of letting it expire.</summary>
public class LeaseReleaseTests
{
    private sealed class FakeLock(string identity, LeaderElectionRecord? current) : ILock
    {
        public LeaderElectionRecord? Current = current;
        public List<LeaderElectionRecord> Updates { get; } = [];
        public bool FailGet { get; init; }
        public string Identity => identity;

        public Task<LeaderElectionRecord> GetAsync(CancellationToken cancellationToken = default)
            => FailGet ? throw new HttpRequestException("api server unreachable") : Task.FromResult(Current!);

        public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a release never creates the Lease");

        public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default)
        {
            Updates.Add(record);
            Current = record;
            return Task.FromResult(true);
        }

        public string Describe() => "ns/lease";
    }

    private static LeaderElectionRecord HeldBy(string holder) => new()
    {
        HolderIdentity = holder,
        LeaseDurationSeconds = 30,
        AcquireTime = DateTime.UtcNow.AddMinutes(-5),
        RenewTime = DateTime.UtcNow,
        LeaderTransitions = 7,
    };

    [Fact]
    public async Task Release_HeldByThisReplica_EmptiesTheHolder_AndShortensTheLease()
    {
        var lease = new FakeLock("me", HeldBy("me"));

        Assert.True(await LeaseRelease.ReleaseAsync(lease, NullLogger.Instance, LeaseRelease.DefaultTimeout));

        var record = Assert.Single(lease.Updates);
        // Empty, not null: the KubernetesClient elector reads a null holder as a missing Lease and
        // tries to create it, which never succeeds — the election would stall.
        Assert.Equal("", record.HolderIdentity);
        Assert.Equal(1, record.LeaseDurationSeconds);
        Assert.Equal(7, record.LeaderTransitions);
        Assert.NotNull(record.AcquireTime);
        Assert.NotNull(record.RenewTime);
    }

    [Fact]
    public async Task Release_HeldByAnotherReplica_LeavesTheLeaseAlone()
    {
        var lease = new FakeLock("me", HeldBy("other"));

        Assert.False(await LeaseRelease.ReleaseAsync(lease, NullLogger.Instance, LeaseRelease.DefaultTimeout));
        Assert.Empty(lease.Updates);
    }

    [Fact]
    public async Task Release_ApiServerUnreachable_ReturnsFalse_WithoutThrowing()
    {
        var lease = new FakeLock("me", HeldBy("me")) { FailGet = true };

        Assert.False(await LeaseRelease.ReleaseAsync(lease, NullLogger.Instance, LeaseRelease.DefaultTimeout));
        Assert.Empty(lease.Updates);
    }

    [Fact]
    public async Task Release_EmptyHolder_IsTakenOnTheNextRetry_ByTheKubernetesClientElector()
    {
        // The standby replica's elector (KubernetesClient) only waits for an unexpired Lease with a
        // non-empty holder. Check that the released record is acquired at once, with the real elector.
        var leader = new FakeLock("me", HeldBy("me"));
        await LeaseRelease.ReleaseAsync(leader, NullLogger.Instance, LeaseRelease.DefaultTimeout);
        var lease = new FakeLock("standby", leader.Current);
        var elector = new LeaderElector(new LeaderElectionConfig(lease)
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            RenewDeadline = TimeSpan.FromSeconds(20),
            RetryPeriod = TimeSpan.FromMilliseconds(100),
        });
        var acquired = new TaskCompletionSource();
        elector.OnStartedLeading += () => acquired.TrySetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = elector.RunUntilLeadershipLostAsync(cts.Token);
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal("standby", lease.Updates[0].HolderIdentity);
    }
}
