using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlAvailabilityGroupDatabaseFinalizerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connectionFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly SqlAvailabilityGroupDatabaseFinalizer _finalizer;

    public SqlAvailabilityGroupDatabaseFinalizerTests()
    {
        _finalizer = new SqlAvailabilityGroupDatabaseFinalizer(
            NullLogger<SqlAvailabilityGroupDatabaseFinalizer>.Instance, _k8s, _connectionFactory,
            _agManager, _eventPublisher);
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
        Assert.Empty(_agManager.RemoveDatabaseFromAgCalls);
    }

    [Fact]
    public async Task FinalizeAsync_NoPrimaryAvailable_Throws()
    {
        var entity = CreateEntity();
        _k8s.SetGet("orders-ag", "default", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "orders-ag" },
            Status = new SqlAvailabilityGroupStatus { PrimaryReplica = null }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _finalizer.FinalizeAsync(entity, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeAsync_AgNotFound_Throws()
    {
        var entity = CreateEntity();
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _finalizer.FinalizeAsync(entity, CancellationToken.None));
    }
}
