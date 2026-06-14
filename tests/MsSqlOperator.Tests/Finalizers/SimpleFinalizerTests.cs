using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlInstanceFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_ReturnsSuccess()
    {
        var finalizer = new SqlInstanceFinalizer(NullLogger<SqlInstanceFinalizer>.Instance);
        var entity = new SqlInstance { Metadata = new V1ObjectMeta { Name = "test" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
    }
}

public class SqlAvailabilityGroupFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_NoSagdbs_ReturnsSuccess()
    {
        var k8s = new FakeKubernetesClient();
        var eventPublisher = new FakeEventPublisher();
        k8s.SetList(new List<SqlAvailabilityGroupDatabase>());
        var finalizer = new SqlAvailabilityGroupFinalizer(NullLogger<SqlAvailabilityGroupFinalizer>.Instance, k8s, eventPublisher);
        var entity = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "test-ag", NamespaceProperty = "default" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
    }

    [Fact]
    public async Task FinalizeAsync_WithSagdbs_BlocksDeletion()
    {
        var k8s = new FakeKubernetesClient();
        var eventPublisher = new FakeEventPublisher();
        var sagdb = new SqlAvailabilityGroupDatabase
        {
            Metadata = new V1ObjectMeta { Name = "db1-sagdb" },
            Spec = new SqlAvailabilityGroupDatabaseSpec
            {
                AvailabilityGroupRef = new ResourceReference { Name = "test-ag" },
                DatabaseName = "DB1"
            }
        };
        k8s.SetList(new List<SqlAvailabilityGroupDatabase> { sagdb });
        var finalizer = new SqlAvailabilityGroupFinalizer(NullLogger<SqlAvailabilityGroupFinalizer>.Instance, k8s, eventPublisher);
        var entity = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "test-ag", NamespaceProperty = "default" } };

        // Finalizer should throw to block deletion
        var act = () => finalizer.FinalizeAsync(entity, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("cannot be deleted", ex.Message);
        Assert.Single(eventPublisher.Calls,
            c => c.Reason == "AgDeletionBlockedByDatabases" && c.Type == "Warning");
    }

    [Fact]
    public async Task FinalizeAsync_ForceCleanup_IgnoresSagdbs()
    {
        var k8s = new FakeKubernetesClient();
        var eventPublisher = new FakeEventPublisher();
        var finalizer = new SqlAvailabilityGroupFinalizer(NullLogger<SqlAvailabilityGroupFinalizer>.Instance, k8s, eventPublisher);
        var entity = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta
            {
                Name = "test-ag",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string>
                {
                    ["mssql-operator.evariops.com/finalizer-policy"] = "force-kubernetes-cleanup"
                }
            }
        };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        // Should NOT try to list SAGDBs
        Assert.Empty(k8s.ListCalls);
    }
}

public class SqlTdeCertificateFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_PreservesCertificate_ReturnsSuccess()
    {
        var finalizer = new SqlTdeCertificateFinalizer(NullLogger<SqlTdeCertificateFinalizer>.Instance);
        var entity = new SqlTdeCertificate { Metadata = new V1ObjectMeta { Name = "test-cert" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
    }
}
