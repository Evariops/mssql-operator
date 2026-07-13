using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// Tests for the AG health checker's replica aggregation — the input to the whole failover machine
/// (untested until then). Focus is the honest reporting of the number
/// of PRIMARY replicas: exactly one ⇒ healthy path; two ⇒ split-brain (no consumer may act on an
/// arbitrary pick); zero ⇒ critical, with a secondary-sourced fallback view.
/// </summary>
public class AgHealthCheckerTests
{
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgManager _agManager = new();

    public AgHealthCheckerTests()
    {
        // Each replica FQDN gets a connection carrying its host in DataSource, so the ag-manager
        // resolvers below can answer per-replica (that is how split-brain is modelled at all).
        _connFactory.ConnectionForHost = host => new SqlConnection($"Server={host},1433");
        _agManager.AgExistsResolver = (_, _) => true; // every replica knows the AG
    }

    private AgHealthChecker Checker() => new(_connFactory, _agManager, NullLogger<AgHealthChecker>.Instance);

    private static SqlAvailabilityGroup Ag(int count = 2) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ha", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = count } }
    };

    private static bool IsReplica(SqlConnection c, int ordinal) =>
        c.DataSource.StartsWith($"ha-{ordinal}.", StringComparison.Ordinal);

    private static AgReplicaStateInfo Replica(string name, string role, string sync = "HEALTHY", string conn = "CONNECTED") =>
        new(Name: name, Role: role, SynchronizationHealth: sync, ConnectionState: conn,
            OperationalState: "ONLINE", IsFailoverReady: sync == "HEALTHY",
            AllowConnections: "ALL", AvailabilityMode: "SYNCHRONOUS_COMMIT");

    [Fact]
    public async Task ExactlyOnePrimary_AllConnected_IsHealthy()
    {
        _agManager.RoleResolver = c => IsReplica(c, 0) ? "PRIMARY" : "SECONDARY";
        _agManager.ReplicaStatesResolver = _ =>
            [Replica("ha-0", "PRIMARY"), Replica("ha-1", "SECONDARY")];

        var report = await Checker().CheckHealthAsync(Ag(), "pw");

        Assert.True(report.IsHealthy);
        Assert.Equal("ha-0", report.PrimaryReplica);
        Assert.Single(report.DetectedPrimaries!);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("SPLIT-BRAIN"));
    }

    [Fact]
    public async Task TwoPrimaries_IsSplitBrain_NoElectedPrimary()
    {
        // Both replicas report PRIMARY — the operator must NOT pick one.
        _agManager.RoleResolver = _ => "PRIMARY";
        _agManager.ReplicaStatesResolver = _ =>
            [Replica("ha-0", "PRIMARY"), Replica("ha-1", "PRIMARY")];

        var report = await Checker().CheckHealthAsync(Ag(), "pw");

        Assert.False(report.IsHealthy);
        Assert.Null(report.PrimaryReplica); // no arbitrary election
        Assert.Equal(2, report.DetectedPrimaries!.Count);
        Assert.Contains(report.Warnings, w => w.Contains("SPLIT-BRAIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoPrimary_ReachableSecondary_ProvidesFallbackView()
    {
        _agManager.RoleResolver = _ => "SECONDARY";
        _agManager.ReplicaStatesResolver = _ =>
            [Replica("ha-0", "SECONDARY"), Replica("ha-1", "SECONDARY")];

        var report = await Checker().CheckHealthAsync(Ag(), "pw");

        Assert.False(report.IsHealthy);
        Assert.Null(report.PrimaryReplica);
        Assert.Empty(report.DetectedPrimaries!);
        // The failover decision still needs data: a secondary-sourced replica view is surfaced.
        Assert.NotEmpty(report.ReplicaStates);
        Assert.Contains(report.Warnings, w => w.Contains("No primary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrimaryUnreachable_SecondaryReachable_ReportedReachabilityIsAccurate()
    {
        // ha-0 (would-be primary) is unreachable; ha-1 answers as secondary.
        _connFactory.ConnectionForHost = host => host.StartsWith("ha-0.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("connection refused")
            : new SqlConnection($"Server={host},1433");
        _agManager.RoleResolver = _ => "SECONDARY";
        _agManager.ReplicaStatesResolver = _ => [Replica("ha-1", "SECONDARY")];

        var report = await Checker().CheckHealthAsync(Ag(), "pw");

        Assert.False(report.IsHealthy);
        Assert.Contains("ha-1", report.ReachableReplicas);
        Assert.DoesNotContain("ha-0", report.ReachableReplicas);
    }

    [Fact]
    public async Task ReplicaWithoutAgMembership_IsFlaggedMissing()
    {
        // ha-1's PV was wiped: it no longer knows the AG.
        _agManager.AgExistsResolver = (c, _) => !IsReplica(c, 1);
        _agManager.RoleResolver = c => IsReplica(c, 0) ? "PRIMARY" : "SECONDARY";
        _agManager.ReplicaStatesResolver = _ => [Replica("ha-0", "PRIMARY")];

        var report = await Checker().CheckHealthAsync(Ag(), "pw");

        Assert.Contains("ha-1", report.ReplicasMissingMembership);
        Assert.False(report.IsHealthy); // missing membership breaks health
    }
}
