using FluentAssertions;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupControllerTests
{
    private readonly Mock<ILogger<SqlAvailabilityGroupController>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<IStatefulSetBuilder> _stsBuilder = new();
    private readonly Mock<IServiceBuilder> _svcBuilder = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly Mock<ICiliumNetworkPolicyBuilder> _netpolBuilder = new();
    private readonly SqlAvailabilityGroupController _controller;

    public SqlAvailabilityGroupControllerTests()
    {
        _controller = new SqlAvailabilityGroupController(_logger.Object, _k8sClient.Object, _stsBuilder.Object, _svcBuilder.Object, _eventPublisher.Object, _netpolBuilder.Object);
    }

    private SqlAvailabilityGroup CreateAg() => new()
    {
        Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default", Uid = "uid-ag", Generation = 1 },
        Spec = new SqlAvailabilityGroupSpec
        {
            Replicas = 3, SynchronousReplicas = 2,
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
            InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022", Edition = "Enterprise" },
            Listener = new AgListenerSpec { Port = 1433 },
            ReadOnlyListener = new AgListenerSpec { Port = 1434 }
        }
    };

    [Fact]
    public async Task ReconcileAsync_CreatesStatefulSetAndServices()
    {
        var entity = CreateAg();
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ss!"u8.ToArray() } } };
        var sts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" } };

        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _stsBuilder.Setup(x => x.BuildForAg(entity, "P@ss!")).Returns(sts);
        _k8sClient.Setup(x => x.GetAsync<V1StatefulSet>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1StatefulSet?)null);
        _k8sClient.Setup(x => x.GetAsync<V1Service>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1Service?)null);
        _svcBuilder.Setup(x => x.BuildHeadless(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<int>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "my-ag-headless", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _svcBuilder.Setup(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<ServiceSpec>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "svc", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity!.Status!.Phase.Should().Be("Running");
        // StatefulSet + headless + listener + RO listener = 4 creates
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1Service>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ReconcileAsync_NoSaPassword_ReturnsWithoutCreating()
    {
        var entity = CreateAg();
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1Secret?)null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
