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
    private readonly FakeVolumeAutoGrower _volumeAutoGrower = new();
    private readonly SqlAvailabilityGroupController _controller;

    public SqlAvailabilityGroupControllerTests()
    {
        _controller = new SqlAvailabilityGroupController(
            NullLogger<SqlAvailabilityGroupController>.Instance, _k8sClient, _stsBuilder, _svcBuilder,
            _eventPublisher, _netpolBuilder, _confBuilder, _machineKeyManager, _connFactory,
            _agManager, _agPodManager, _agHealthChecker, _failoverOrchestrator,
            _rollingUpdateOrchestrator, _sqlServerManager, _smkManager, _saConverger, _rolloutBudget, _volumeAutoGrower,
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
            ReadOnlyListener = edition == "Enterprise" ? new AgReadOnlyListenerSpec { Port = 1434 } : null
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
    public async Task ReconcileAsync_NoPrimaryReachable_KeepsTheLastKnownPrimary()
    {
        // The pass cleared status.primaryReplica; when the unreachable threshold was met
        // the orchestrator promoted a secondary without fencing the isolated primary.
        var entity = CreateAg();
        // A UID of its own: the infrastructure cadence is kept per UID across controller instances,
        // and sharing it would skip the slow pass of the next test.
        entity.Metadata.Uid = "uid-ag-last-known-primary";
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: false, PrimaryReplica: null,
            ReplicaStates: [], DatabaseStates: [], Warnings: ["No primary replica found — AG is in critical state"],
            ReachableReplicas: ["my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("my-ag-0", result.Entity!.Status!.PrimaryReplica);
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaBackOnBlankVolumes_IsReprovisionedAndRejoined()
    {
        // Its volumes lost, my-ag-2 came back without master key, certificate,
        // endpoint or AG; the peers still trusted its lost certificate. The operator only said so.
        var entity = CreateAg();
        entity.Metadata.Uid = "uid-ag-blank-replica";
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: false, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: ["Replica my-ag-2: AG 'my-ag' not found"],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: ["my-ag-2"]);
        static bool On(Microsoft.Data.SqlClient.SqlConnection c, string pod) => c.DataSource.StartsWith(pod + ".", StringComparison.Ordinal);
        _agManager.CertificateResolver = (c, cert) => cert == "HadrCert_my-ag-2"
            ? (On(c, "my-ag-2") ? [2, 2] : [9, 9]) // the new certificate / the peers' stale copy
            : [1];

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Contains(_agManager.JoinHosts, h => h.StartsWith("my-ag-2.", StringComparison.Ordinal));
        foreach (var peer in new[] { "my-ag-0", "my-ag-1" })
        {
            Assert.Contains(_agManager.RemoveTrustedCalls, c => c.Host.StartsWith(peer + ".", StringComparison.Ordinal) && c.Cert == "HadrCert_my-ag-2");
            Assert.Contains(_agManager.ImportTrustedCalls, c => c.Host.StartsWith(peer + ".", StringComparison.Ordinal) && c.Cert == "HadrCert_my-ag-2");
            Assert.Contains(_agManager.ImportTrustedCalls, c => c.Host.StartsWith("my-ag-2.", StringComparison.Ordinal) && c.Cert == $"HadrCert_{peer}");
        }
        Assert.Contains("ReplicaReprovisioned", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_NeedsAttention_RaisedForAStoppedRollout_ClearedOnceTheOperatorIsBackInCharge()
    {
        // One condition the on-call alerts on, True only while a human decision is pending.
        var entity = CreateAg();
        entity.Metadata.Uid = "uid-ag-needs-attention";
        entity.Status = new SqlAvailabilityGroupStatus
        {
            Phase = "Running",
            PrimaryReplica = "my-ag-0",
            RollingUpdate = new AgRollingUpdateStatus { Phase = "Failed" },
        };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);

        var stopped = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var attention = stopped.Entity!.Status!.Conditions!.Single(c => c.Type == "NeedsAttention");
        Assert.Equal("True", attention.Status);
        Assert.Equal("RollingUpdateFailed", attention.Reason);

        stopped.Entity.Status.RollingUpdate = new AgRollingUpdateStatus { Phase = "Idle" };
        var resumed = await _controller.ReconcileAsync(stopped.Entity, CancellationToken.None);

        Assert.Equal("False", resumed.Entity!.Status!.Conditions!.Single(c => c.Type == "NeedsAttention").Status);
    }

    [Fact]
    public async Task ReconcileAsync_VolumeThatCannotGrow_NeedsAttention_UntilItHasRoom()
    {
        // A volume at its ceiling (or on a StorageClass without expansion) is a human's call.
        var entity = CreateAg();
        entity.Metadata.Uid = "uid-ag-volume-full";
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);
        _volumeAutoGrower.Blocked = "The log volume of my-ag-0 is 99% full and already at its ceiling (20Gi).";

        var full = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("True", full.Entity!.Status!.Conditions!.Single(c => c.Type == "VolumeFull").Status);
        var attention = full.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention");
        Assert.Equal("True", attention.Status);
        Assert.Equal("VolumeFull", attention.Reason);

        _volumeAutoGrower.Blocked = null;
        var room = await _controller.ReconcileAsync(full.Entity, CancellationToken.None);

        Assert.Equal("False", room.Entity!.Status!.Conditions!.Single(c => c.Type == "VolumeFull").Status);
        Assert.Equal("False", room.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention").Status);
    }

    [Fact]
    public async Task ReconcileAsync_EndpointKeyStillUnusable_NeedsAttention_NothingRegenerated()
    {
        // A wrong database master key password leaves the endpoint key unusable; the operator
        // no longer regenerates the Service Master Key and hands the call to a human.
        var entity = CreateAg();
        entity.Metadata.Uid = "uid-ag-endpoint-key";
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);
        _agManager.EndpointKeyFailure = new MsSqlOperator.Services.SqlServer.EndpointKeyUnusableException(
            MsSqlOperator.Services.SqlServer.EndpointKeyUnusableException.MasterKeyPasswordRejected, "15313");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var key = result.Entity!.Status!.Conditions!.Single(c => c.Type == "EndpointKeyUsable");
        Assert.Equal("False", key.Status);
        Assert.Equal("MasterKeyPasswordRejected", key.Reason);
        Assert.Contains("never regenerates", key.Message);
        var attention = result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention");
        Assert.Equal("True", attention.Status);
        Assert.Equal("EndpointKeyUnusable", attention.Reason);
        Assert.Contains("EndpointKeyUnusable", _eventPublisher.Reasons);
    }

    [Theory]
    [InlineData(new[] { 15313 }, "MasterKeyPasswordRejected")]
    [InlineData(new[] { 15466 }, "ServiceMasterKeyUndecryptable")]
    [InlineData(new[] { 18456 }, "EndpointKeyUnusable")]
    public void EndpointKeyUnusable_IsClassifiedByErrorNumber(int[] numbers, string expected)
        => Assert.Equal(expected, MsSqlOperator.Services.SqlServer.EndpointKeyUnusableException.Classify(numbers));

    /// <summary>my-ag-0 primary, my-ag-1 synchronized, my-ag-2 as the primary sees it (or not at all).</summary>
    private SqlAvailabilityGroup WithThirdReplica(string uid, string? connection, bool reachable = false)
    {
        var entity = CreateAg();
        entity.Metadata.Uid = uid;
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        List<MsSqlOperator.Services.SqlServer.AgReplicaStateInfo> states =
        [
            new("my-ag-0", "PRIMARY", "HEALTHY", "CONNECTED", "ONLINE", true, "ALL", "SYNCHRONOUS_COMMIT"),
            new("my-ag-1", "SECONDARY", "HEALTHY", "CONNECTED", "ONLINE", true, "ALL", "SYNCHRONOUS_COMMIT"),
        ];
        if (connection is not null)
            states.Add(new("my-ag-2", "SECONDARY", "NOT_HEALTHY", connection, "ONLINE", false, "ALL", "SYNCHRONOUS_COMMIT"));
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: false, PrimaryReplica: "my-ag-0", ReplicaStates: states, DatabaseStates: [], Warnings: [],
            ReachableReplicas: reachable ? ["my-ag-0", "my-ag-1", "my-ag-2"] : ["my-ag-0", "my-ag-1"], ReplicasMissingMembership: []);
        _agManager.ReplicaStatesResolver = _ => states;
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaDisconnectedForLong_HoldingTheLog_IsTakenOutOfTheAg()
    {
        // A secondary that never came back held the primary's whole log back;
        // no backup can truncate it, and the log grew until every write failed with 9002.
        var entity = WithThirdReplica("uid-ag-dead-replica", "DISCONNECTED");
        entity.Status!.ReplicaDisconnectedSince = new Dictionary<string, DateTime> { ["my-ag-2"] = DateTime.UtcNow.AddMinutes(-31) };
        _agManager.HeldLogs = [new MsSqlOperator.Services.SqlServer.HeldLogInfo("OrdersDB", 92)];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(["my-ag-2"], _agManager.RemovedReplicas);
        Assert.True(result.Entity!.Status!.RemovedReplicas!.ContainsKey("my-ag-2"));
        Assert.Null(result.Entity.Status.ReplicaDisconnectedSince);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "ReplicaRemoved" && c.Message.Contains("OrdersDB (92% full)"));
    }

    [Theory]
    [InlineData(10, 92, 30)]   // not disconnected for long enough yet
    [InlineData(31, 60, 30)]   // the log it holds is far from full
    [InlineData(31, 92, 0)]    // removal disabled
    public async Task ReconcileAsync_DisconnectedReplica_StaysInTheAg_UnlessItHasHeldAFullLogForLong(int minutes, int percent, int after)
    {
        var entity = WithThirdReplica($"uid-ag-dead-replica-{minutes}-{percent}-{after}", "DISCONNECTED");
        entity.Spec.Failover = new AgFailoverSpec { RemoveDisconnectedReplicaAfterMinutes = after };
        entity.Status!.ReplicaDisconnectedSince = new Dictionary<string, DateTime> { ["my-ag-2"] = DateTime.UtcNow.AddMinutes(-minutes) };
        _agManager.HeldLogs = [new MsSqlOperator.Services.SqlServer.HeldLogInfo("OrdersDB", percent)];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.RemovedReplicas);
        Assert.True(result.Entity!.Status!.ReplicaDisconnectedSince!.ContainsKey("my-ag-2"));
    }

    [Fact]
    public async Task ReconcileAsync_RemovedReplicaAnswersAgain_IsAddedBackAndReseeded()
    {
        var entity = WithThirdReplica("uid-ag-readd", connection: null, reachable: true);
        entity.Status!.RemovedReplicas = new Dictionary<string, DateTime> { ["my-ag-2"] = DateTime.UtcNow.AddHours(-2) };
        _agManager.AgDatabaseNames = ["OrdersDB"];
        _agManager.EndpointExists = true;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DroppedLocalAgHosts, h => h.StartsWith("my-ag-2.", StringComparison.Ordinal));
        Assert.Contains(_agManager.DropSecondaryCopyCalls, c => c.Host.StartsWith("my-ag-2.", StringComparison.Ordinal) && c.Database == "OrdersDB");
        Assert.Equal(["my-ag-2"], _agManager.AddedReplicas);
        Assert.Contains(_agManager.JoinHosts, h => h.StartsWith("my-ag-2.", StringComparison.Ordinal));
        Assert.Null(result.Entity!.Status!.RemovedReplicas);
        Assert.Contains("ReplicaReadded", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaMissingFromTheAg_IsNotAddedBackWhileItDoesNotAnswer()
    {
        var entity = WithThirdReplica("uid-ag-readd-unreachable", connection: null);

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.AddedReplicas);
    }

    private static V1Pod PodOn(string name, string node) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default" },
        Spec = new V1PodSpec { NodeName = node },
    };

    [Fact]
    public async Task ReconcileAsync_MachineKeyReplaced_MachineKeyChanged_NeedsAttention_OriginalKept()
    {
        // The machine key Secret now holds another key than the one the data was created with.
        var entity = CreateAg();
        entity.Metadata.Uid = "uid-ag-machine-key-changed";
        var original = new string('a', 64);
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0", MachineKeySha256 = original };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);
        _machineKeyManager.Sha256 = new string('b', 64);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(original, result.Entity!.Status!.MachineKeySha256);
        Assert.Equal("True", result.Entity.Status.Conditions!.Single(c => c.Type == "MachineKeyChanged").Status);
        Assert.Equal("MachineKeyChanged", result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention").Reason);
    }

    [Fact]
    public async Task ReconcileAsync_ReplicaTakenOutByHand_HoldingACopy_IsNeitherAddedBackNorEmptied()
    {
        // A replica removed by hand, whose database was restored WITH RECOVERY, may hold data of
        // its own. It is not in RemovedReplicas: it is reported, and nothing touches it.
        var entity = WithThirdReplica("uid-ag-drift", connection: null, reachable: true);
        _agManager.AgDatabaseNames = ["OrdersDB"];
        _agManager.UserDatabasesResolver = c => c.DataSource!.StartsWith("my-ag-2.", StringComparison.Ordinal) ? ["OrdersDB"] : [];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.AddedReplicas);
        Assert.Empty(_agManager.DropSecondaryCopyCalls);
        Assert.DoesNotContain(_agManager.DroppedLocalAgHosts, h => h.StartsWith("my-ag-2.", StringComparison.Ordinal));
        var drift = result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReplicaMembershipDrift");
        Assert.Equal("True", drift.Status);
        Assert.Contains("my-ag-2 (holds OrdersDB)", drift.Message);
        var attention = result.Entity.Status.Conditions!.Single(c => c.Type == "NeedsAttention");
        Assert.Equal("ReplicaMembershipDrift", attention.Reason);
    }

    [Fact]
    public async Task ReconcileAsync_NewReplicaWithoutCopies_IsAddedToTheAg()
    {
        // A replica that holds none of the AG's databases (scale-up, blank volumes) is added.
        var entity = WithThirdReplica("uid-ag-new-replica", connection: null, reachable: true);
        _agManager.AgDatabaseNames = ["OrdersDB"];
        _agManager.EndpointExists = true;

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(["my-ag-2"], _agManager.AddedReplicas);
        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c.Type == "ReplicaMembershipDrift" && c.Status == "True");
    }

    [Fact]
    public async Task ReconcileAsync_RemovedReplicaWithAWritableCopy_IsNotAddedBack()
    {
        // Even a replica the operator took out keeps a copy brought online by hand: the drop
        // refuses it, and the replica is reported instead of added back.
        var entity = WithThirdReplica("uid-ag-readd-writable", connection: null, reachable: true);
        entity.Status!.RemovedReplicas = new Dictionary<string, DateTime> { ["my-ag-2"] = DateTime.UtcNow.AddHours(-2) };
        _agManager.AgDatabaseNames = ["OrdersDB"];
        _agManager.DropSecondaryCopyRefused = c => c.DataSource!.StartsWith("my-ag-2.", StringComparison.Ordinal);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.AddedReplicas);
        Assert.Contains("SecondaryCopyNotDropped", _eventPublisher.Reasons);
        Assert.Equal("True", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReplicaMembershipDrift").Status);
        Assert.NotNull(result.Entity.Status.RemovedReplicas);
    }

    [Theory]
    [InlineData("node-a", "node-a", "node-a", "my-ag-0, my-ag-1, my-ag-2 on node-a")]
    [InlineData("node-a", "node-b", "node-a", "my-ag-0, my-ag-2 on node-a")]
    public async Task ReconcileAsync_ReplicasSharingANode_AreReported(string node0, string node1, string node2, string expected)
    {
        // The three replicas ran on the one amd64 node; rebooting it took the whole AG
        // down, and nothing had said the AG did not survive the loss of a node.
        var entity = RunningWithSecondaries($"uid-ag-colocated-{node1}", "CONNECTED");
        _agPodManager.AgPods = [PodOn("my-ag-0", node0), PodOn("my-ag-1", node1), PodOn("my-ag-2", node2)];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var colocated = result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReplicasColocated");
        Assert.Equal("True", colocated.Status);
        Assert.Contains(expected, colocated.Message);
        Assert.Contains("ReplicasColocated", _eventPublisher.Reasons);
        Assert.DoesNotContain(result.Entity.Status.Conditions!, c => c.Type == "NeedsAttention" && c.Status == "True");
    }

    [Fact]
    public async Task ReconcileAsync_ReplicasOnNodesOfTheirOwn_NothingToReport()
    {
        var entity = RunningWithSecondaries("uid-ag-spread", "CONNECTED");
        _agPodManager.AgPods = [PodOn("my-ag-0", "node-a"), PodOn("my-ag-1", "node-b"), PodOn("my-ag-2", "node-c")];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c.Type == "ReplicasColocated" && c.Status == "True");
    }

    private SqlAvailabilityGroup RunningWithSecondaries(string uid, string connection, bool? fallback = null)
    {
        var entity = CreateAg();
        entity.Metadata.Uid = uid;
        if (fallback is { } f)
            entity.Spec.ReadOnlyListener!.FallbackToPrimary = f;
        entity.Spec.ReadOnlyRouting = new AgReadOnlyRoutingSpec { Enabled = true };
        entity.Status = new SqlAvailabilityGroupStatus { Phase = "Running", PrimaryReplica = "my-ag-0" };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        var health = connection == "CONNECTED" ? "HEALTHY" : "NOT_HEALTHY";
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-0",
            ReplicaStates:
            [
                new("my-ag-0", "PRIMARY", "HEALTHY", "CONNECTED", "ONLINE", true, "ALL", "SYNCHRONOUS_COMMIT"),
                new("my-ag-1", "SECONDARY", health, connection, "ONLINE", connection == "CONNECTED", "ALL", "SYNCHRONOUS_COMMIT"),
                new("my-ag-2", "SECONDARY", health, connection, "ONLINE", connection == "CONNECTED", "ALL", "SYNCHRONOUS_COMMIT"),
            ],
            DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-0", "my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_NoSecondaryCanServeReads_ThePrimaryServesTheReadOnlyListener_UntilOneCan()
    {
        // During a partition of the HADR traffic the read-only listener had no
        // endpoint at all while the primary was healthy; no read was served until it ended.
        var partitioned = RunningWithSecondaries("uid-ag-ro-fallback", "DISCONNECTED");

        var fallback = await _controller.ReconcileAsync(partitioned, CancellationToken.None);

        Assert.True(_agPodManager.ReadOnlyTargets["my-ag-0"]);
        Assert.False(_agPodManager.ReadOnlyTargets["my-ag-1"]);
        Assert.False(_agPodManager.ReadOnlyTargets["my-ag-2"]);
        Assert.Equal("True", fallback.Entity!.Status!.Conditions!.Single(c => c.Type == "ReadOnlyOnPrimary").Status);
        Assert.Contains("ReadOnlyFallbackToPrimary", _eventPublisher.Reasons);
        Assert.Equal(["my-ag-0"], _agManager.ReadOnlyRoutingLists.Last());

        var healed = RunningWithSecondaries("uid-ag-ro-fallback", "CONNECTED");
        healed.Status = fallback.Entity.Status;
        var back = await _controller.ReconcileAsync(healed, CancellationToken.None);

        Assert.False(_agPodManager.ReadOnlyTargets["my-ag-0"]);
        Assert.True(_agPodManager.ReadOnlyTargets["my-ag-1"]);
        Assert.Equal("False", back.Entity!.Status!.Conditions!.Single(c => c.Type == "ReadOnlyOnPrimary").Status);
        Assert.Contains("ReadOnlyBackOnSecondaries", _eventPublisher.Reasons);
        Assert.Equal(["my-ag-1", "my-ag-2", "my-ag-0"], _agManager.ReadOnlyRoutingLists.Last());
    }

    [Fact]
    public async Task ReconcileAsync_FallbackToPrimaryDisabled_ReadOnlyListenerStaysOffThePrimary()
    {
        var partitioned = RunningWithSecondaries("uid-ag-ro-no-fallback", "DISCONNECTED", fallback: false);

        var result = await _controller.ReconcileAsync(partitioned, CancellationToken.None);

        Assert.False(_agPodManager.ReadOnlyTargets["my-ag-0"]);
        Assert.DoesNotContain(result.Entity!.Status!.Conditions!, c => c.Type == "ReadOnlyOnPrimary" && c.Status == "True");
        Assert.Empty(_agManager.ReadOnlyRoutingLists);
    }

    private SqlAvailabilityGroup JustFailedOverFrom(string uid, TimeSpan ago)
    {
        var entity = CreateAg();
        entity.Metadata.Uid = uid;
        entity.Status = new SqlAvailabilityGroupStatus
        {
            Phase = "FailingOver",
            PrimaryReplica = "my-ag-1",
            LastFailover = new AgLastFailoverStatus { From = "my-ag-0", To = "my-ag-1", Timestamp = DateTime.UtcNow - ago },
        };
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };
        _agHealthChecker.Report = new AgHealthReport(
            IsHealthy: true, PrimaryReplica: "my-ag-1",
            ReplicaStates: [], DatabaseStates: [], Warnings: [],
            ReachableReplicas: ["my-ag-1", "my-ag-2"], ReplicasMissingMembership: []);
        // The recreated pod of the fenced primary, reached by its IP (the DNS may still name the old pod).
        _k8sClient.SetGet("my-ag-0", "default", new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "my-ag-0", NamespaceProperty = "default", Uid = "uid-recreated" },
            Status = new V1PodStatus { PodIP = "10.0.0.5" },
        });
        return entity;
    }

    [Fact]
    public async Task ReconcileAsync_FencedPrimaryComesBackAsPrimary_IsTakenOfflineOnItsFirstAnswer()
    {
        // The recreated pod of a fenced primary starts the AG as PRIMARY; the health pass
        // noticed it 10–25 s later, long enough for a secondary to follow it.
        var entity = JustFailedOverFrom("uid-ag-resurrected", TimeSpan.FromSeconds(10));
        _agManager.RoleResolver = c => c.DataSource == "10.0.0.5" ? "PRIMARY" : "SECONDARY";

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_agManager.DemoteCalls, a => a == "my-ag");
        Assert.Contains(_agPodManager.RoleLabelCalls, c => c.Pod == "my-ag-0" && c.Role == "resolving");
        Assert.Contains("ResurrectedPrimaryContained", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_PreviousPrimaryAnswersAsSecondary_NothingToContain()
    {
        var entity = JustFailedOverFrom("uid-ag-not-resurrected", TimeSpan.FromSeconds(10));
        _agManager.RoleResolver = _ => "SECONDARY";

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DemoteCalls);
        Assert.DoesNotContain("ResurrectedPrimaryContained", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_PreviousPrimaryStillStarting_IsWatchedUntilItComesOnlineAsPrimary()
    {
        // The recreated instance answers about 1 s before its AG comes online. Taking
        // that first answer (no role yet) for a settled one ended the watch just before it turned PRIMARY.
        var entity = JustFailedOverFrom("uid-ag-starting", TimeSpan.FromSeconds(10));
        _agManager.InstanceUptime = TimeSpan.FromSeconds(2);
        var answers = 0;
        _agManager.RoleResolver = c => c.DataSource != "10.0.0.5" ? "SECONDARY" : ++answers == 1 ? null : "PRIMARY";

        var starting = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DemoteCalls);
        Assert.Equal(TimeSpan.FromSeconds(1), starting.RequeueAfter);

        await _controller.ReconcileAsync(starting.Entity!, CancellationToken.None);

        Assert.Single(_agManager.DemoteCalls, a => a == "my-ag");
        Assert.Contains("ResurrectedPrimaryContained", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReconcileAsync_PreviousPrimaryResolvingForLong_EndsTheWatch()
    {
        // A previous primary demoted in place stays RESOLVING until it is given the secondary role:
        // it settled its role long ago, there is nothing to wait for.
        var entity = JustFailedOverFrom("uid-ag-resolving", TimeSpan.FromSeconds(10));
        _agManager.RoleResolver = c => c.DataSource == "10.0.0.5" ? "RESOLVING" : "SECONDARY";

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Single(_connFactory.ProbeCalls, c => c.Host == "10.0.0.5");
        Assert.Empty(_agManager.DemoteCalls);
    }

    [Fact]
    public async Task ReconcileAsync_ResurrectedPrimaryContained_IsNotPolledAgain()
    {
        var entity = JustFailedOverFrom("uid-ag-contained-once", TimeSpan.FromSeconds(10));
        _agManager.RoleResolver = c => c.DataSource == "10.0.0.5" ? "PRIMARY" : "SECONDARY";
        var first = await _controller.ReconcileAsync(entity, CancellationToken.None);
        _connFactory.ProbeCalls.Clear();

        var second = await _controller.ReconcileAsync(first.Entity!, CancellationToken.None);

        Assert.DoesNotContain(_connFactory.ProbeCalls, c => c.Host == "10.0.0.5");
        Assert.Single(_agManager.DemoteCalls);
        Assert.NotEqual(TimeSpan.FromSeconds(1), second.RequeueAfter);
    }

    [Fact]
    public async Task ReconcileAsync_ForcedFailoverThisPass_ReturnsAtOnce_AndTheNextPassComesWithinASecond()
    {
        // Waiting for the previous primary inside the failover pass held back the
        // pass that restores writes on the new primary — 20 s of "lacks a quorum". The pass returns at
        // once and the AG is passed every second while the previous primary is watched.
        var entity = JustFailedOverFrom("uid-ag-same-pass", TimeSpan.Zero);
        entity.Status!.Phase = "Running";
        entity.Status.PrimaryReplica = "my-ag-0";
        entity.Status.LastFailover = null;
        _failoverOrchestrator.Outcome = MsSqlOperator.Services.SqlServer.FailoverOutcome.ForcedFailover;
        _failoverOrchestrator.OnReconcile = ag =>
        {
            ag.Status!.PrimaryReplica = "my-ag-1";
            ag.Status.LastFailover = new AgLastFailoverStatus { From = "my-ag-0", To = "my-ag-1", Timestamp = DateTime.UtcNow };
        };
        _agManager.RoleResolver = c => c.DataSource == "10.0.0.5" ? "PRIMARY" : "SECONDARY";

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_agManager.DemoteCalls);
        Assert.Equal(TimeSpan.FromSeconds(1), result.RequeueAfter);
    }

    [Fact]
    public async Task ReconcileAsync_FailoverLongAgo_PreviousPrimaryIsNoLongerWatched()
    {
        var entity = JustFailedOverFrom("uid-ag-old-failover", TimeSpan.FromMinutes(5));
        _agManager.RoleResolver = _ => "PRIMARY";

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.DoesNotContain(_connFactory.ProbeCalls, c => c.Host == "10.0.0.5");
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
        entity.Spec.ReadOnlyListener = new AgReadOnlyListenerSpec { Port = 1434 }; // Should be ignored
        SeedSecrets();
        _stsBuilder.Result = new V1StatefulSet { Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" }, Spec = new V1StatefulSetSpec() };

        await _controller.ReconcileAsync(entity, CancellationToken.None);

        // Headless + R/W listener = 2 services (no RO for Standard)
        Assert.Equal(2, _k8sClient.Applied<V1Service>().Count());
    }
}
