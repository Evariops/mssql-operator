using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
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
    private readonly FakeSaPasswordConverger _saConverger = new();
    private readonly FakeRolloutBudget _rolloutBudget = new();
    private readonly MsSqlOperator.Operator.Registration.OperatorOptions _options = new();
    private readonly SqlInstanceController _controller;

    public SqlInstanceControllerTests()
    {
        _controller = new SqlInstanceController(
            NullLogger<SqlInstanceController>.Instance, _k8sClient, _stsBuilder, _svcBuilder, _eventPublisher,
            _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory, _sqlServerManager, _smkManager,
            new FakeFullTextManager(), _saConverger, _rolloutBudget, _options, new MsSqlOperator.Services.Metrics.OperatorMetrics());
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
    public async Task ReconcileAsync_FormerSelector_RecreatesTheStatefulSetAroundItsPods()
    {
        // StatefulSets built before the selector was reduced to the identity label carry version
        // and edition in an immutable field — recreated once (orphan delete), the pods are adopted.
        var entity = CreateInstance();
        _k8sClient.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } });
        var identity = new Dictionary<string, string> { ["mssql-operator.evariops.com/instance"] = "test-sql" };
        _stsBuilder.Result = new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" },
            Spec = new V1StatefulSetSpec { Selector = new V1LabelSelector { MatchLabels = identity } },
        };
        _k8sClient.SetGet("test-sql", "default", new V1StatefulSet
        {
            Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" },
            Spec = new V1StatefulSetSpec
            {
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>(identity)
                    {
                        ["mssql-operator.evariops.com/version"] = "2019",
                        ["mssql-operator.evariops.com/edition"] = "Developer",
                    },
                },
            },
        });

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(("test-sql", "default"), Assert.Single(_k8sClient.DeleteStatefulSetOrphanCalls));
        Assert.Single(_k8sClient.Applied<V1StatefulSet>());
        Assert.Contains("StatefulSetRecreated", _eventPublisher.Reasons);
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
    public async Task ReconcileAsync_PodDisruptionBudgetEnabled_CreatesNoPdb_AndDeletesTheOneItOwns()
    {
        // The maxUnavailable=0 PDB blocked every drain of the node for good.
        var entity = SeededInstance();
        entity.Spec.PodDisruptionBudget = new PodDisruptionBudgetSpec { Enabled = true };
        _k8sClient.SetGet("test-sql-pdb", "default", new V1PodDisruptionBudget
        {
            Metadata = new V1ObjectMeta
            {
                Name = "test-sql-pdb",
                NamespaceProperty = "default",
                OwnerReferences = [new V1OwnerReference { Kind = "SqlInstance", Name = "test-sql", Uid = "uid-1", Controller = true }],
            },
        });

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_k8sClient.Applied<V1PodDisruptionBudget>());
        Assert.Equal("test-sql-pdb", Assert.Single(_k8sClient.Deleted<V1PodDisruptionBudget>()).Metadata.Name);
    }

    [Fact]
    public async Task ReconcileAsync_LeavesAPdbItDoesNotOwn()
    {
        var entity = SeededInstance();
        _k8sClient.SetGet("test-sql-pdb", "default", new V1PodDisruptionBudget
        {
            Metadata = new V1ObjectMeta { Name = "test-sql-pdb", NamespaceProperty = "default" },
        });

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_k8sClient.Deleted<V1PodDisruptionBudget>());
    }

    private SqlInstance SeededInstance()
    {
        _k8sClient.SetGet("sa-secret", "default", new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ssw0rd!"u8.ToArray() } } });
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } };
        return CreateInstance();
    }

    private static ConditionStatus? Attention(ReconciliationResult<SqlInstance> result)
        => result.Entity!.Status!.Conditions!.FirstOrDefault(c => c.Type == "NeedsAttention");

    [Fact]
    public async Task ReconcileAsync_MachineKeyChanged_NeedsAttention()
    {
        // The instance had no NeedsAttention at all.
        var entity = SeededInstance();
        entity.Status = new SqlInstanceStatus { MachineKeySha256 = new string('1', 64) };
        _machineKeyManager.Sha256 = new string('2', 64);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(("True", "MachineKeyChanged"), (Attention(result)!.Status, Attention(result)!.Reason));
    }

    [Fact]
    public async Task ReconcileAsync_RecreatedWithAnotherMachineKey_TheRegistryCatchesIt_AndNoRestartIsMade()
    {
        // The status is gone with the recreated CR; the control record still knows the
        // original fingerprint — the new key is not taken as the reference, and the pod is not restarted.
        var entity = SeededInstance();
        _k8sClient.StoreWrites = true;
        _k8sClient.SetGet("instance-test-sql", "default", new SqlControlRecord
        {
            Metadata = new V1ObjectMeta { Name = "instance-test-sql", NamespaceProperty = "default" },
            Spec = new SqlControlRecordSpec { Keys = new ControlKeys { MachineKeySha256 = new string('1', 64) } },
        });
        _k8sClient.SetGet("test-sql", "default", new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "test-sql", NamespaceProperty = "default" } });
        _machineKeyManager.Sha256 = new string('2', 64);
        var options = new MsSqlOperator.Operator.Registration.OperatorOptions();
        var registry = new MsSqlOperator.Services.Control.ControlRegistry(_k8sClient, new MsSqlOperator.Services.Control.OperatorEpoch(_k8sClient, "ops"));
        var controller = new SqlInstanceController(
            NullLogger<SqlInstanceController>.Instance, _k8sClient, _stsBuilder, _svcBuilder, _eventPublisher,
            _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory, _sqlServerManager, _smkManager,
            new FakeFullTextManager(), _saConverger, _rolloutBudget, options, new MsSqlOperator.Services.Metrics.OperatorMetrics(), null,
            new MsSqlOperator.Services.Control.KeyFingerprints(registry, options,
                NullLogger<MsSqlOperator.Services.Control.KeyFingerprints>.Instance));

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(("True", "MachineKeyChanged"), (Attention(result)!.Status, Attention(result)!.Reason));
        Assert.Empty(_k8sClient.Applied<V1StatefulSet>());
        Assert.Contains("RestartsBlocked", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_MachineKeySecretMissing_NeedsAttention()
    {
        var entity = SeededInstance();
        _machineKeyManager.ThrowOnEnsure = new MsSqlOperator.Services.Kubernetes.MachineKeySecretMissingException("secret mk not found");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(("True", "MachineKeySecretMissing"), (Attention(result)!.Status, Attention(result)!.Reason));
    }

    [Fact]
    public async Task ReconcileAsync_SaPasswordRefused_NeedsAttention()
    {
        var entity = SeededInstance();
        _saConverger.Outcome = MsSqlOperator.Services.Kubernetes.SaConvergence.Failed;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(("True", "SaPasswordNotConverged"), (Attention(result)!.Status, Attention(result)!.Reason));
    }

    [Fact]
    public async Task ReconcileAsync_SaPasswordPending_NoAttention()
    {
        var entity = SeededInstance();
        _saConverger.Outcome = MsSqlOperator.Services.Kubernetes.SaConvergence.Pending;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotEqual("True", Attention(result)?.Status);
    }

    [Theory]
    [InlineData("Terminating", 20, true)]
    [InlineData("Terminating", 10, false)]  // Within the relocation delay, the breakout may still act
    [InlineData("NodeNotReady", 20, true)]
    [InlineData("NodeNotReady", 10, false)]
    [InlineData("NodeReady", 20, false)] // still starting on a healthy node: a wait, not a human's call
    public async Task ReconcileAsync_PodStuckOnALostNode_NeedsAttention(string situation, int minutes, bool attention)
    {
        var clock = new FakeTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        var entity = SeededInstance();
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "test-sql-0", NamespaceProperty = "default" },
            Spec = new V1PodSpec { NodeName = "node-a" },
            Status = new V1PodStatus { Conditions = [new V1PodCondition { Type = "Ready", Status = "False", LastTransitionTime = now.AddMinutes(-minutes) }] },
        };
        if (situation == "Terminating")
            pod.Metadata.DeletionTimestamp = now.AddMinutes(-minutes);
        _k8sClient.SetGet("test-sql-0", "default", pod);
        _k8sClient.SetGet<V1Node>("node-a", null, new V1Node
        {
            Metadata = new V1ObjectMeta { Name = "node-a" },
            Status = new V1NodeStatus { Conditions = [new V1NodeCondition { Type = "Ready", Status = situation == "NodeReady" ? "True" : "Unknown" }] },
        });
        var controller = new SqlInstanceController(
            NullLogger<SqlInstanceController>.Instance, _k8sClient, _stsBuilder, _svcBuilder, _eventPublisher,
            _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory, _sqlServerManager, _smkManager,
            new FakeFullTextManager(), _saConverger, _rolloutBudget, _options, new MsSqlOperator.Services.Metrics.OperatorMetrics(), clock);

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(attention, Attention(result)?.Status == "True");
        if (attention)
            Assert.Equal(situation == "Terminating" ? "PodStuck" : "InstanceOnLostNode", Attention(result)!.Reason);
    }

    [Fact]
    public async Task DeletedAsync_ReturnsSuccess()
    {
        var result = await _controller.DeletedAsync(CreateInstance(), CancellationToken.None);
        Assert.NotNull(result.Entity);
    }
}
