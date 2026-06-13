using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupDatabaseControllerTests
{
    private readonly Mock<ILogger<SqlAvailabilityGroupDatabaseController>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly Mock<ISqlConnectionFactory> _connectionFactory = new();
    private readonly Mock<ISqlAgManager> _agManager = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly SqlAvailabilityGroupDatabaseController _controller;

    public SqlAvailabilityGroupDatabaseControllerTests()
    {
        _controller = new SqlAvailabilityGroupDatabaseController(
            _logger.Object, _k8s.Object, _connectionFactory.Object,
            _agManager.Object, _eventPublisher.Object);
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
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup?)null);
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        result.Entity!.Status!.Phase.Should().Be("WaitingForAg");
    }

    [Fact]
    public async Task ReconcileAsync_PendingWithAgNotRunning_SetsWaitingForAg()
    {
        var entity = CreateEntity("Pending");
        var ag = CreateAg("Initializing");
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ag);
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity!.Status!.Phase.Should().Be("WaitingForAg");
    }

    [Fact]
    public async Task ReconcileAsync_FailedPhase_ReEvaluatesFromScratch()
    {
        // Failed is not terminal: the controller re-evaluates from scratch so transient
        // errors self-recover. With the AG absent it should fall back to WaitingForAg.
        var entity = CreateEntity("Failed");
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup?)null);
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        result.Entity!.Status!.Phase.Should().Be("WaitingForAg");
        // Failed re-reconciles: the AG reference is resolved again.
        _k8s.Verify(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_PendingConditions_HasAgReadyFalse()
    {
        var entity = CreateEntity("Pending");
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup?)null);
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var agReadyCondition = result.Entity!.Status!.Conditions!.FirstOrDefault(c => c.Type == "AgReady");
        agReadyCondition.Should().NotBeNull();
        agReadyCondition!.Status.Should().Be("False");
    }

    [Fact]
    public async Task ReconcileAsync_StalledSecondary_ResumesOnThatSecondaryNotThePrimary()
    {
        // After a failover the former primary lands in NOT SYNCHRONIZING (SUSPEND_FROM_PARTNER).
        // HADR RESUME must be issued ON that secondary — issuing it on the primary connection is a
        // no-op, which left the AG without redundancy after every failover.
        var entity = CreateEntity("Synchronized");
        var ag = CreateAg("Running");
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ag);
        _k8s.Setup(k => k.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);
        _connectionFactory
            .Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SqlConnection());
        _agManager
            .Setup(x => x.GetDatabaseStatesAsync(It.IsAny<SqlConnection>(), "orders-ag", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AgDatabaseStateInfo("OrdersDB", "orders-ag-0", "PRIMARY", "SYNCHRONIZED", true, 0, 0),
                new AgDatabaseStateInfo("OrdersDB", "orders-ag-1", "SECONDARY", "NOT SYNCHRONIZING", false, 0, 0),
            ]);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // The resume connects to the stalled secondary's headless FQDN, not the primary's.
        _connectionFactory.Verify(x => x.CreateConnectionAsync(
            "orders-ag-1.orders-ag-headless.default.svc.cluster.local", 1433, "pw", "master", It.IsAny<CancellationToken>()), Times.Once);
        _agManager.Verify(x => x.ResumeDataMovementAsync(It.IsAny<SqlConnection>(), "OrdersDB", It.IsAny<CancellationToken>()), Times.Once);
    }
}
