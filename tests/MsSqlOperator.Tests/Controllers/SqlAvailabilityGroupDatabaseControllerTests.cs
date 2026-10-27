using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupDatabaseControllerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connectionFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly SqlAvailabilityGroupDatabaseController _controller;

    public SqlAvailabilityGroupDatabaseControllerTests()
    {
        _controller = new SqlAvailabilityGroupDatabaseController(
            NullLogger<SqlAvailabilityGroupDatabaseController>.Instance, _k8s, _connectionFactory,
            _agManager, _eventPublisher);
    }

    private static SqlAvailabilityGroupDatabase CreateEntity(string phase = "Pending") => new()
    {
        Metadata = new V1ObjectMeta { Name = "orders-sagdb", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupDatabaseSpec
        {
            AvailabilityGroupRef = new ResourceReference { Name = "orders-ag" },
            DatabaseName = "OrdersDB",
            SeedingMode = "Automatic"
        },
        Status = new SqlAvailabilityGroupDatabaseStatus { Phase = phase }
    };

    private static SqlAvailabilityGroup CreateAg(string phase = "Running") => new()
    {
        Metadata = new V1ObjectMeta { Name = "orders-ag", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupSpec
        {
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" }
        },
        Status = new SqlAvailabilityGroupStatus
        {
            Phase = phase,
            PrimaryReplica = "orders-ag-0"
        }
    };

    [Fact]
    public async Task ReconcileAsync_PendingWithNoAg_SetsWaitingForAg()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_PendingWithAgNotRunning_SetsWaitingForAg()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet("orders-ag", "default", CreateAg("Initializing"));

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_FailedPhase_ReEvaluatesFromScratch()
    {
        // Failed is not terminal: the controller re-evaluates from scratch so transient
        // errors self-recover. With the AG absent it should fall back to WaitingForAg.
        var entity = CreateEntity("Failed");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
        // Failed re-reconciles: the AG reference is resolved again.
        Assert.Single(_k8s.GetCalls, c => c.Type == "SqlAvailabilityGroup" && c.Name == "orders-ag" && c.Ns == "default");
    }

    [Fact]
    public async Task ReconcileAsync_PendingConditions_HasAgReadyFalse()
    {
        var entity = CreateEntity("Pending");
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var agReadyCondition = result.Entity!.Status!.Conditions!.FirstOrDefault(c => c.Type == "AgReady");
        Assert.NotNull(agReadyCondition);
        Assert.Equal("False", agReadyCondition!.Status);
    }

    [Fact]
    public async Task ReconcileAsync_StalledSecondary_ResumesOnThatSecondaryNotThePrimary()
    {
        // After a failover the former primary lands in NOT SYNCHRONIZING (SUSPEND_FROM_PARTNER).
        // HADR RESUME must be issued ON that secondary — issuing it on the primary connection is a
        // no-op, which left the AG without redundancy after every failover.
        var entity = CreateEntity("Synchronized");
        _k8s.SetGet("orders-ag", "default", CreateAg("Running"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // The resume connects to the stalled secondary's headless FQDN, not the primary's.
        Assert.Single(_connectionFactory.Calls,
            c => c.Host == "orders-ag-1.orders-ag-headless.default.svc.cluster.local" && c.Port == 1433 && c.Password == "pw" && c.Database == "master");
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
    }

    [Fact]
    public async Task ReconcileAsync_AgDegradedBySuspendedSecondary_StillResumesIt()
    {
        // After a failover the AG is Degraded precisely because the former primary's
        // database is suspended — and this controller, the one that resumes it, waited for Running.
        var entity = CreateEntity("Synchronized");
        _k8s.SetGet("orders-ag", "default", CreateAg("Degraded"));
        _k8s.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _agManager.DatabaseStates =
        [
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
            new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
        ];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotEqual("WaitingForAg", result.Entity!.Status!.Phase);
        Assert.Single(_agManager.ResumeDataMovementCalls, d => d == "OrdersDB");
    }

    [Fact]
    public async Task ReconcileAsync_AgDegradedWithoutPrimary_Waits()
    {
        var entity = CreateEntity("Pending");
        var ag = CreateAg("Degraded");
        ag.Status!.PrimaryReplica = null;
        _k8s.SetGet("orders-ag", "default", ag);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("WaitingForAg", result.Entity!.Status!.Phase);
    }
}
