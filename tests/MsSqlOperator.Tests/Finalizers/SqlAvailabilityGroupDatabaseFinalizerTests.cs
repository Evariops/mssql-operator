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
    public async Task FinalizeAsync_PrimaryMoving_RemovalWaits()
    {
        // Removing the database while the rolling update moves the primary acts on a replica
        // that is about to stop being the primary.
        var entity = CreateEntity();
        _k8s.SetGet("orders-ag", "default", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "orders-ag" },
            Status = new SqlAvailabilityGroupStatus
            {
                Phase = "Running", PrimaryReplica = "orders-ag-0",
                RollingUpdate = new AgRollingUpdateStatus { Phase = RollingUpdatePhases.FailingOverForUpdate },
            },
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _finalizer.FinalizeAsync(entity, CancellationToken.None));

        Assert.Contains("rolling update", ex.Message);
        Assert.Empty(_agManager.RemoveDatabaseFromAgCalls);
    }

    // An AG that no longer exists has no primary to wait for — waiting kept the namespace
    // Terminating for good.
    [Fact]
    public async Task FinalizeAsync_AgGone_SucceedsWithoutSql()
    {
        var entity = CreateEntity();
        _k8s.SetGet<SqlAvailabilityGroup>("orders-ag", "default", null);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Empty(_agManager.RemoveDatabaseFromAgCalls);
        Assert.Contains("AgRemovalSkipped", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task FinalizeAsync_AgBeingDeleted_SucceedsWithoutWaitingForAPrimary()
    {
        // Namespace deletion: the namespace controller deletes the pods while the CRs wait on their
        // finalizers, so the primary is never reachable again.
        var entity = CreateEntity();
        _k8s.SetGet("orders-ag", "default", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "orders-ag", DeletionTimestamp = DateTime.UtcNow },
            Status = new SqlAvailabilityGroupStatus { PrimaryReplica = null }
        });

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Empty(_agManager.RemoveDatabaseFromAgCalls);
    }
}
