using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
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
    private readonly FakeSaPasswordConverger _saConverger = new();
    private readonly FakeRolloutBudget _rolloutBudget = new();
    private readonly SqlAvailabilityGroupController _controller;

    public SqlAvailabilityGroupControllerTests()
    {
        _controller = new SqlAvailabilityGroupController(
            NullLogger<SqlAvailabilityGroupController>.Instance, _k8sClient, _stsBuilder, _svcBuilder,
            _eventPublisher, _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory,
            _agManager, _agPodManager, _agHealthChecker, _failoverOrchestrator,
            _rollingUpdateOrchestrator, _sqlServerManager, _smkManager, _saConverger, _rolloutBudget,
            new MsSqlOperator.Operator.Registration.OperatorOptions(),
            new MsSqlOperator.Services.Metrics.OperatorMetrics(), TimeProvider.System);
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

    // ── "Failed" must never be a persisted, dead-end phase ──────────────────
    // Regression grid for the phase machine: every "not ready" trigger must leave the AG on a
    // ROUTABLE phase (one the switch dispatches) with a Ready=False condition — never on a "Failed"
    // phase the switch would no-op forever. The old ReconcileAsync_NoSaPassword_TransitionsToFailed
    // test encoded exactly the bug this replaces.

    [Fact]
    public async Task ReconcileAsync_NoSaPassword_StaysPendingWithReadyFalse_NeverFailed()
    {
        var entity = CreateAg();
        _k8sClient.SetGet<V1Secret>("sa-secret", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotEqual("Failed", result.Entity!.Status!.Phase);
        Assert.Equal("Pending", result.Entity.Status.Phase); // not-yet-started AG stays Pending
        var ready = result.Entity.Status.Conditions!.Single(c => c.Type == "Ready");
        Assert.Equal("False", ready.Status);
        Assert.Equal("SaPasswordNotFound", ready.Reason);
        Assert.Empty(_k8sClient.Applied<V1StatefulSet>());
    }

    [Fact]
    public async Task ReconcileAsync_MachineKeyMissingWhilePending_StaysPending_NeverFailed()
    {
        var entity = CreateAg();
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _machineKeyManager.ThrowOnEnsure = new MachineKeySecretMissingException("machineKeySecretRef 'mk' not found");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotEqual("Failed", result.Entity!.Status!.Phase);
        Assert.Equal("Pending", result.Entity.Status.Phase);
        var ready = result.Entity.Status.Conditions!.Single(c => c.Type == "Ready");
        Assert.Equal("False", ready.Status);
        Assert.Equal("MachineKeySecretMissing", ready.Reason);
    }

    [Fact]
    public async Task ReconcileAsync_MachineKeyMissingWhileRunning_GoesDegraded_ThenSelfHealsToRunning()
    {
        // Step 1: an operational AG whose machine-key secret vanishes must go Degraded (routable),
        // NOT "Failed". Under the old code it went to a dead-end "Failed" and never recovered.
        var entity = CreateAg();
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _machineKeyManager.ThrowOnEnsure = new MachineKeySecretMissingException("machineKeySecretRef 'mk' not found");

        var degraded = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("Degraded", degraded.Entity!.Status!.Phase);
        Assert.Equal("MachineKeySecretMissing", degraded.Entity.Status.Conditions!.Single(c => c.Type == "Ready").Reason);

        // Step 2: the cause is fixed (secret returns) and the AG is healthy → the SAME entity, still
        // on a routable phase, self-heals to Running. This is the behaviour "Failed" made impossible.
        _machineKeyManager.ThrowOnEnsure = null;
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0"], ReplicasMissingMembership: []);

        var healed = await _controller.ReconcileAsync(degraded.Entity, CancellationToken.None);

        Assert.Equal("Running", healed.Entity!.Status!.Phase);
        Assert.Equal("True", healed.Entity.Status.Conditions!.Single(c => c.Type == "Ready").Status);
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
