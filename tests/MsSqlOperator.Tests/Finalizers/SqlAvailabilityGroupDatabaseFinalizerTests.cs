using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlAvailabilityGroupDatabaseFinalizerTests
{
    private readonly Mock<ILogger<SqlAvailabilityGroupDatabaseFinalizer>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly Mock<ISqlConnectionFactory> _connectionFactory = new();
    private readonly Mock<ISqlAgManager> _agManager = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly SqlAvailabilityGroupDatabaseFinalizer _finalizer;

    public SqlAvailabilityGroupDatabaseFinalizerTests()
    {
        _finalizer = new SqlAvailabilityGroupDatabaseFinalizer(
            _logger.Object, _k8s.Object, _connectionFactory.Object,
            _agManager.Object, _eventPublisher.Object);
    }

    private static SqlAvailabilityGroupDatabase CreateEntity() => new()
    {
        Metadata = new V1ObjectMeta { Name = "orders-sagdb", NamespaceProperty = "default" },
        Spec = new SqlAvailabilityGroupDatabaseSpec
        {
            AvailabilityGroupRef = new ResourceReference { Name = "orders-ag" },
            DatabaseName = "OrdersDB"
        }
    };

    [Fact]
    public async Task FinalizeAsync_ForceCleanup_SkipsSqlAndSucceeds()
    {
        var entity = CreateEntity();
        entity.Metadata.Annotations = new Dictionary<string, string>
        {
            ["mssql-operator.evariops.com/finalizer-policy"] = "force-kubernetes-cleanup"
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        Assert.NotNull(result.Entity);
        _agManager.Verify(a => a.RemoveDatabaseFromAgAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FinalizeAsync_NoPrimaryAvailable_Throws()
    {
        var entity = CreateEntity();
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlAvailabilityGroup
            {
                Metadata = new V1ObjectMeta { Name = "orders-ag" },
                Status = new SqlAvailabilityGroupStatus { PrimaryReplica = null }
            });
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _finalizer.FinalizeAsync(entity, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeAsync_AgNotFound_Throws()
    {
        var entity = CreateEntity();
        _k8s.Setup(k => k.GetAsync<SqlAvailabilityGroup>("orders-ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroup?)null);
        _k8s.Setup(k => k.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroupDatabase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlAvailabilityGroupDatabase e, CancellationToken _) => e);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _finalizer.FinalizeAsync(entity, CancellationToken.None));
    }
}
