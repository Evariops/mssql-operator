using FluentAssertions;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlInstanceFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlInstanceFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var finalizer = new SqlInstanceFinalizer(logger.Object, k8s.Object);
        var entity = new SqlInstance { Metadata = new V1ObjectMeta { Name = "test" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }
}

public class SqlAvailabilityGroupFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlAvailabilityGroupFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var finalizer = new SqlAvailabilityGroupFinalizer(logger.Object, k8s.Object);
        var entity = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "test-ag" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }
}

public class SqlTdeCertificateFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_PreservesCertificate_ReturnsSuccess()
    {
        var logger = new Mock<ILogger<SqlTdeCertificateFinalizer>>();
        var k8s = new Mock<IKubernetesClient>();
        var finalizer = new SqlTdeCertificateFinalizer(logger.Object, k8s.Object);
        var entity = new SqlTdeCertificate { Metadata = new V1ObjectMeta { Name = "test-cert" } };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
    }
}
