using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlInstanceFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlInstanceFinalizer>>();
        var finalizer = new SqlInstanceFinalizer(logger.Object);
        var entity = new SqlInstance { Metadata = new V1ObjectMeta { Name = "test" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }
}

public class SqlAvailabilityGroupFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_NoSagdbs_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlAvailabilityGroupFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var eventPublisher = new Mock<IEventPublisher>();
        k8s.Setup(k => k.ListAsync<SqlAvailabilityGroupDatabase>("default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlAvailabilityGroupDatabase>());
        var finalizer = new SqlAvailabilityGroupFinalizer(logger.Object, k8s.Object, eventPublisher.Object);
        var entity = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "test-ag", NamespaceProperty = "default" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }

    [Fact]
    public async Task FinalizeAsync_WithSagdbs_BlocksDeletion()
    {
        var logger = new Mock<ILogger<SqlAvailabilityGroupFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var eventPublisher = new Mock<IEventPublisher>();
        var sagdb = new SqlAvailabilityGroupDatabase
        {
            Metadata = new V1ObjectMeta { Name = "db1-sagdb" },
            Spec = new SqlAvailabilityGroupDatabaseSpec
            {
                AvailabilityGroupRef = new ResourceReference { Name = "test-ag" },
                DatabaseName = "DB1"
            }
        };
        k8s.Setup(k => k.ListAsync<SqlAvailabilityGroupDatabase>("default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SqlAvailabilityGroupDatabase> { sagdb });
        var finalizer = new SqlAvailabilityGroupFinalizer(logger.Object, k8s.Object, eventPublisher.Object);
        var entity = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "test-ag", NamespaceProperty = "default" } };

        // Finalizer should throw to block deletion
        var act = () => finalizer.FinalizeAsync(entity, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be deleted*");
        eventPublisher.Verify(e => e.PublishAsync(It.IsAny<SqlAvailabilityGroup>(), "AgDeletionBlockedByDatabases",
            It.IsAny<string>(), "Warning", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalizeAsync_ForceCleanup_IgnoresSagdbs()
    {
        var logger = new Mock<ILogger<SqlAvailabilityGroupFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var eventPublisher = new Mock<IEventPublisher>();
        var finalizer = new SqlAvailabilityGroupFinalizer(logger.Object, k8s.Object, eventPublisher.Object);
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

        result.Entity.Should().NotBeNull();
        // Should NOT try to list SAGDBs
        k8s.Verify(k => k.ListAsync<SqlAvailabilityGroupDatabase>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class SqlTdeCertificateFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_PreservesCertificate_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlTdeCertificateFinalizer>>();
        var finalizer = new SqlTdeCertificateFinalizer(logger.Object);
        var entity = new SqlTdeCertificate { Metadata = new V1ObjectMeta { Name = "test-cert" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }
}
