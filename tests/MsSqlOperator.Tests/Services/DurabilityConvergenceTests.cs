using MsSqlOperator.Services.Control;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>The availability modes brought to Quorum, one replica at a time.</summary>
public class DurabilityConvergenceTests
{
    private static AgReplicaStateInfo Replica(string name, string role, string mode, string health = "HEALTHY", string connection = "CONNECTED")
        => new(name, role, health, connection, "ONLINE", true, AvailabilityMode: mode);

    private static readonly string[] Designated = ["ag-0", "ag-1", "ag-2"];

    [Fact]
    public void AnAsynchronousSecondary_IsRaisedFirst_OneAtATime()
    {
        var (next, converged) = DurabilityConvergence.Plan(Designated,
        [
            Replica("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-1", "SECONDARY", "ASYNCHRONOUS_COMMIT"),
            Replica("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT"),
        ]);

        Assert.Equal(("ag-1", false), (next, converged));
    }

    [Fact]
    public void ARaisedSecondaryNotSynchronizedYet_IsWaitedFor()
    {
        var (next, converged) = DurabilityConvergence.Plan(Designated,
        [
            Replica("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT", health: "PARTIALLY_HEALTHY"),
            Replica("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT"),
        ]);

        Assert.Equal((null, false), (next, converged));
    }

    [Fact]
    public void EverySynchronousAndHealthy_HasConverged()
        => Assert.Equal((null, true), DurabilityConvergence.Plan(Designated,
        [
            Replica("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-2", "SECONDARY", "SYNCHRONOUS_COMMIT"),
        ]));

    [Fact]
    public void ADisconnectedAsynchronousSecondary_IsNotTouched_NorCountedConverged()
        => Assert.Equal((null, false), DurabilityConvergence.Plan(Designated,
        [
            Replica("ag-0", "PRIMARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-1", "SECONDARY", "SYNCHRONOUS_COMMIT"),
            Replica("ag-2", "SECONDARY", "ASYNCHRONOUS_COMMIT", health: "NOT_HEALTHY", connection: "DISCONNECTED"),
        ]));
}
