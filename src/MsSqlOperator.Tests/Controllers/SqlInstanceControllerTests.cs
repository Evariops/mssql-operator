using FluentAssertions;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Controllers;

public class SqlInstanceControllerTests
{
    private readonly Mock<ILogger<SqlInstanceController>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<IStatefulSetBuilder> _stsBuilder = new();
    private readonly Mock<IServiceBuilder> _svcBuilder = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly Mock<ICiliumNetworkPolicyBuilder> _netpolBuilder = new();
    private readonly Mock<IMssqlConfBuilder> _confBuilder = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlServerManager> _sqlServerManager = new();
    private readonly SqlInstanceController _controller;

    public SqlInstanceControllerTests()
    {
        _confBuilder.Setup(x => x.Build(It.IsAny<SqlConfigurationSpec>(), It.IsAny<MemorySpec>(), It.IsAny<StorageSpec>())).Returns("[EULA]\naccepteula = Y\n");
        _confBuilder.Setup(x => x.ComputeHash(It.IsAny<string>())).Returns("testhash");
        _controller = new SqlInstanceController(_logger.Object, _k8sClient.Object, _stsBuilder.Object, _svcBuilder.Object, _eventPublisher.Object, _netpolBuilder.Object, _confBuilder.Object, _connFactory.Object, _sqlServerManager.Object);
    }

    private SqlInstance CreateInstance(string name = "test-sql", string ns = "default") => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Uid = "uid-1", Generation = 1 },
        Spec = new SqlInstanceSpec
        {
            Mode = "SingleNode", Version = "2022", Edition = "Developer",
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" }
        }
    };

    [Fact]
    public async Task ReconcileAsync_WithSaPassword_CreatesStatefulSetAndService()
    {
        var entity = CreateInstance();
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } };
        var sts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } };
        var svc = new V1Service { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() };

        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _stsBuilder.Setup(x => x.Build(entity, It.IsAny<string?>())).Returns(sts);
        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>(It.IsAny<string>(), "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1ConfigMap?)null);
        _k8sClient.Setup(x => x.GetAsync<V1StatefulSet>("test-sql", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1StatefulSet?)null);
        _svcBuilder.Setup(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<ServiceSpec>(), It.IsAny<V1OwnerReference>())).Returns(svc);
        _k8sClient.Setup(x => x.GetAsync<V1Service>("test-sql", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1Service?)null);
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlInstance>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlInstance e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _k8sClient.Verify(x => x.CreateAsync(sts, It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(x => x.CreateAsync(svc, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_NoSaPasswordSecret_SetsFailedStatus()
    {
        var entity = CreateInstance();
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1Secret?)null);
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlInstance>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlInstance e, CancellationToken _) => e);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        result.Entity!.Status!.Phase.Should().Be("Failed");
    }

    [Fact]
    public async Task ReconcileAsync_ExistingStatefulSet_UpdatesInsteadOfCreating()
    {
        var entity = CreateInstance();
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } };
        var sts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } };
        var existingSts = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default", ResourceVersion = "123" } };
        var svc = new V1Service { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" }, Spec = new V1ServiceSpec() };

        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _stsBuilder.Setup(x => x.Build(entity, It.IsAny<string?>())).Returns(sts);
        _k8sClient.Setup(x => x.GetAsync<V1ConfigMap>(It.IsAny<string>(), "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1ConfigMap?)null);
        _k8sClient.Setup(x => x.GetAsync<V1StatefulSet>("test-sql", "default", It.IsAny<CancellationToken>())).ReturnsAsync(existingSts);
        _svcBuilder.Setup(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<ServiceSpec>(), It.IsAny<V1OwnerReference>())).Returns(svc);
        _k8sClient.Setup(x => x.GetAsync<V1Service>("test-sql", "default", It.IsAny<CancellationToken>())).ReturnsAsync((V1Service?)null);
        _k8sClient.Setup(x => x.UpdateStatusAsync(It.IsAny<SqlInstance>(), It.IsAny<CancellationToken>())).ReturnsAsync((SqlInstance e, CancellationToken _) => e);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        _k8sClient.Verify(x => x.UpdateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1StatefulSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletedAsync_ReturnsSuccess()
    {
        var entity = CreateInstance();
        var result = await _controller.DeletedAsync(entity, CancellationToken.None);
        result.Entity.Should().NotBeNull();
    }
}
