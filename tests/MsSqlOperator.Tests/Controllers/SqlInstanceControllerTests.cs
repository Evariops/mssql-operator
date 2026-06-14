using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlInstanceControllerTests
{
    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly FakeStatefulSetBuilder _stsBuilder = new();
    private readonly FakeServiceBuilder _svcBuilder = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly FakeCiliumNetworkPolicyBuilder _netpolBuilder = new();
    private readonly FakeMssqlConfBuilder _confBuilder = new();
    private readonly FakeMachineKeyManager _machineKeyManager = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlServerManager _sqlServerManager = new();
    private readonly FakeServiceMasterKeyManager _smkManager = new();
    private readonly SqlInstanceController _controller;

    public SqlInstanceControllerTests()
    {
        _controller = new SqlInstanceController(
            NullLogger<SqlInstanceController>.Instance, _k8sClient, _stsBuilder, _svcBuilder, _eventPublisher,
            _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory, _sqlServerManager, _smkManager,
            new FakeSqlUserManager(), new MsSqlOperator.Services.Metrics.OperatorMetrics());
    }

    private static SqlInstance CreateInstance(string name = "test-sql", string ns = "default") => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Uid = "uid-1", Generation = 1 },
        Spec = new SqlInstanceSpec
        {
            Mode = "SingleNode",
            Version = "2022",
            Edition = "Developer",
            SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" }
        }
    };

    [Fact]
    public async Task ReconcileAsync_WithSaPassword_CreatesStatefulSetAndService()
    {
        var entity = CreateInstance();
        _k8sClient.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } });
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } };

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Single(_k8sClient.Applied<V1StatefulSet>());
        Assert.Single(_k8sClient.Applied<V1Service>());
    }

    [Fact]
    public async Task ReconcileAsync_NoSaPasswordSecret_SetsFailedStatus()
    {
        var entity = CreateInstance();
        _k8sClient.SetGet<V1Secret>("sa-secret", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("Failed", result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_ExistingStatefulSet_UpdatesInsteadOfCreating()
    {
        var entity = CreateInstance();
        _k8sClient.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } });
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } };
        _k8sClient.SetGet("test-sql", "default", new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default", ResourceVersion = "123" } });

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // SSA — one ApplyAsync regardless of pre-existence, no GET+PUT.
        Assert.Single(_k8sClient.Applied<V1StatefulSet>());
    }

    [Fact]
    public async Task DeletedAsync_ReturnsSuccess()
    {
        var result = await _controller.DeletedAsync(CreateInstance(), CancellationToken.None);
        Assert.NotNull(result.Entity);
    }
}
