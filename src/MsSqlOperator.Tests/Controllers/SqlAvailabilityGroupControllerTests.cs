using FluentAssertions;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupControllerTests
{
    private readonly Mock<ILogger<SqlAvailabilityGroupController>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<IStatefulSetBuilder> _stsBuilder = new();
    private readonly Mock<IServiceBuilder> _svcBuilder = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly Mock<ICiliumNetworkPolicyBuilder> _netpolBuilder = new();
    private readonly Mock<IMssqlConfBuilder> _confBuilder = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlAgManager> _agManager = new();
    private readonly Mock<IAgPodManager> _agPodManager = new();
    private readonly Mock<IAgHealthChecker> _agHealthChecker = new();
    private readonly Mock<ISqlServerManager> _sqlServerManager = new();
    private readonly SqlAvailabilityGroupController _controller;

    public SqlAvailabilityGroupControllerTests()
    {
        _confBuilder.Setup(x => x.Build(It.IsAny<SqlConfigurationSpec>(), It.IsAny<MemorySpec>(), It.IsAny<StorageSpec>())).Returns("[EULA]\naccepteula = Y\n");
        _confBuilder.Setup(x => x.ComputeHash(It.IsAny<string>())).Returns("testhash");
        _controller = new SqlAvailabilityGroupController(
            _logger.Object, _k8sClient.Object, _stsBuilder.Object, _svcBuilder.Object,
            _eventPublisher.Object, _netpolBuilder.Object, _confBuilder.Object, _connFactory.Object,
            _agManager.Object, _agPodManager.Object, _agHealthChecker.Object,
            _sqlServerManager.Object);
    }

    private static SqlAvailabilityGroup CreateAg(string edition = "Enterprise") => new()
    {
        Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default", Uid = "uid-ag", Generation = 1 },
        Spec = new SqlAvailabilityGroupSpec
        {
            Edition = edition,
            Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
            MasterKeyPasswordSecretRef = new SecretReference { Name = "mk-secret", Key = "password" },
            HadrCertificate = new AgHadrCertificateSpec
            {
                Source = "Generate",
                Generate = new CertificateGenerateSpec { Subject = "Test cert" },
                SecretName = "cert-secret",
                EncryptionPasswordSecretRef = new SecretReference { Name = "cert-pwd", Key = "password" }
            },
            InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022" },
            Listener = new AgListenerSpec { Port = 1433 },
            ReadOnlyListener = edition == "Enterprise" ? new AgListenerSpec { Port = 1434 } : null
        }
    };

    [Fact]
    public async Task ReconcileAsync_Pending_CreatesInfraAndTransitionsToInitializing()
    {
        var entity = CreateAg();
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ss!"u8.ToArray() } } };
        var sts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };

        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _stsBuilder.Setup(x => x.BuildForAg(entity, It.IsAny<string?>())).Returns(sts);
        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1ConfigMap?)null);
        _k8sClient.Setup(x => x.GetAsync<V1StatefulSet>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1StatefulSet?)null);
        _k8sClient.Setup(x => x.GetAsync<V1Service>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1Service?)null);
        _svcBuilder.Setup(x => x.BuildHeadless(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<int>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "my-ag-headless", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _svcBuilder.Setup(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<ServiceSpec>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "svc", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity!.Status!.Phase.Should().Be("Initializing");
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_NoSaPassword_TransitionsToFailed()
    {
        var entity = CreateAg();
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1Secret?)null);
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity!.Status!.Phase.Should().Be("Failed");
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_Standard_NoReadOnlyListener()
    {
        var entity = CreateAg("Standard");
        entity.Spec.Replicas = new AgReplicaTopologySpec { Count = 2, SynchronousCount = 2 };
        entity.Spec.ReadOnlyListener = new AgListenerSpec { Port = 1434 }; // Should be ignored
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ss!"u8.ToArray() } } };
        var sts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };

        _k8sClient.Setup(x => x.GetAsync<V1Secret>(It.IsAny<string>(), "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _stsBuilder.Setup(x => x.BuildForAg(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<string?>())).Returns(sts);
        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1ConfigMap?)null);
        _k8sClient.Setup(x => x.GetAsync<V1StatefulSet>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1StatefulSet?)null);
        _k8sClient.Setup(x => x.GetAsync<V1Service>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((V1Service?)null);
        _svcBuilder.Setup(x => x.BuildHeadless(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<int>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "headless", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _svcBuilder.Setup(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<ServiceSpec>(), It.IsAny<V1OwnerReference>()))
            .Returns(new V1Service { Metadata = new V1ObjectMeta { Name = "svc", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() });
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlAvailabilityGroup>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlAvailabilityGroup e, CancellationToken _) => e);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // Headless + R/W listener = 2 services (no RO for Standard)
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1Service>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
