using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlAvailabilityGroupControllerTests
{
    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly FakeStatefulSetBuilder _stsBuilder = new();
    private readonly FakeServiceBuilder _svcBuilder = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly FakeCiliumNetworkPolicyBuilder _netpolBuilder = new();
    private readonly FakeMssqlConfBuilder _confBuilder = new();
    private readonly FakeMachineKeyManager _machineKeyManager = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeAgPodManager _agPodManager = new();
    private readonly FakeAgHealthChecker _agHealthChecker = new();
    private readonly FakeAgFailoverOrchestrator _failoverOrchestrator = new();
    private readonly FakeAgRollingUpdateOrchestrator _rollingUpdateOrchestrator = new();
    private readonly FakeSqlServerManager _sqlServerManager = new();
    private readonly FakeServiceMasterKeyManager _smkManager = new();
    private readonly SqlAvailabilityGroupController _controller;

    public SqlAvailabilityGroupControllerTests()
    {
        _controller = new SqlAvailabilityGroupController(
            NullLogger<SqlAvailabilityGroupController>.Instance, _k8sClient, _stsBuilder, _svcBuilder,
            _eventPublisher, _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory,
            _agManager, _agPodManager, _agHealthChecker, _failoverOrchestrator,
            _rollingUpdateOrchestrator, _sqlServerManager, _smkManager,
            new MsSqlOperator.Services.Metrics.OperatorMetrics());
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

    private void SeedSecrets()
    {
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ss!"u8.ToArray() } } };
        _k8sClient.SetGet("sa-secret", "default", secret);
        _k8sClient.SetGet("mk-secret", "default", secret);
        _k8sClient.SetGet("cert-pwd", "default", secret);
    }

    [Fact]
    public async Task ReconcileAsync_Pending_CreatesInfraAndTransitionsToInitializing()
    {
        var entity = CreateAg();
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("Initializing", result.Entity!.Status!.Phase);
        Assert.Single(_k8sClient.Applied<V1StatefulSet>());
    }

    [Fact]
    public async Task ReconcileAsync_NoSaPassword_TransitionsToFailed()
    {
        var entity = CreateAg();
        _k8sClient.SetGet<V1Secret>("sa-secret", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("Failed", result.Entity!.Status!.Phase);
        Assert.Empty(_k8sClient.Applied<V1StatefulSet>());
    }

    [Fact]
    public async Task ReconcileAsync_Standard_NoReadOnlyListener()
    {
        var entity = CreateAg("Standard");
        entity.Spec.Replicas = new AgReplicaTopologySpec { Count = 2, SynchronousCount = 2 };
        entity.Spec.ReadOnlyListener = new AgListenerSpec { Port = 1434 }; // Should be ignored
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // Headless + R/W listener = 2 services (no RO for Standard)
        Assert.Equal(2, _k8sClient.Applied<V1Service>().Count());
    }
}
