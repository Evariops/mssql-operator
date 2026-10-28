using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using ResourceQuantity = k8s.Models.ResourceQuantity;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class VolumeAutoGrowerTests
{
    private const long Mi = 1L << 20;

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlAgManager _agManager = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly VolumeAutoGrower _sut;

    public VolumeAutoGrowerTests()
    {
        _sut = new VolumeAutoGrower(NullLogger<VolumeAutoGrower>.Instance, _k8s, _connFactory, _agManager, _eventPublisher);
        _k8s.SetGet("fast", null, new V1StorageClass { Metadata = new V1ObjectMeta { Name = "fast" }, AllowVolumeExpansion = true });
    }

    private static SqlAvailabilityGroup CreateAg(AgVolumeAutoGrowSpec? policy = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default", Uid = "uid-ag" },
        Spec = new SqlAvailabilityGroupSpec
        {
            InstanceTemplate = new AgInstanceTemplateSpec
            {
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec { Size = "8Gi", StorageClassName = "fast" },
                    Log = new StorageVolumeSpec { Size = "4Gi", StorageClassName = "fast" },
                },
            },
            VolumeAutoGrow = policy,
        },
    };

    /// <summary>The PVC of a volume, its filesystem caught up with its request unless said otherwise.</summary>
    private V1PersistentVolumeClaim Pvc(string name, string size, string? capacity = null, string storageClass = "fast")
    {
        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default" },
            Spec = new V1PersistentVolumeClaimSpec
            {
                StorageClassName = storageClass,
                Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) } },
            },
            Status = new V1PersistentVolumeClaimStatus { Capacity = new Dictionary<string, ResourceQuantity> { ["storage"] = new(capacity ?? size) } },
        };
        _k8s.SetGet(name, "default", pvc);
        return pvc;
    }

    /// <summary>What the replica's SQL Server reports: its log volume (as on a test cluster) and its data volume.</summary>
    private void Volumes(long logTotalMb, long logFreeMb, long dataTotalMb = 8000, long dataFreeMb = 4000) =>
        _agManager.FileVolumesResolver = _ =>
        [
            new FileVolumeInfo("/var/opt/mssql/data/ShopDB.mdf", dataTotalMb * Mi, dataFreeMb * Mi),
            new FileVolumeInfo("/var/opt/mssql/data/mastlog.ldf", dataTotalMb * Mi, dataFreeMb * Mi),
            new FileVolumeInfo("/var/opt/mssql/log/ShopDB_log.ldf", logTotalMb * Mi, logFreeMb * Mi),
        ];

    private static string Requested(V1PersistentVolumeClaim pvc) => pvc.Spec.Resources.Requests["storage"].ToString();

    [Fact]
    public async Task ReplacedVolume_IsBroughtUpToItsPeers_WhateverTheGrowthMode()
    {
        // Data-chaos-2 lost and recreated at the declared 8Gi while its peers had
        // grown to 12 and 18Gi; the 9.3 GB copy never fit and seeding failed on every attempt.
        var ag = CreateAg(new AgVolumeAutoGrowSpec { Mode = "Disabled" });
        ag.Spec.Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 };
        Pvc("data-ag-0", "12Gi");
        Pvc("data-ag-1", "18Gi");
        var replaced = Pvc("data-ag-2", "8Gi");
        Pvc("log-ag-0", "14Gi");
        Pvc("log-ag-1", "14Gi");
        var log = Pvc("log-ag-2", "14Gi");

        var blocked = await _sut.ReconcileAsync(ag, ["ag-0", "ag-1", "ag-2"], "pw", CancellationToken.None);

        Assert.Null(blocked);
        Assert.Equal("18Gi", Requested(replaced));
        Assert.Equal("14Gi", Requested(log));
        Assert.Contains("VolumeExpanded", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task ReplacedVolume_StorageClassWithoutExpansion_IsReported()
    {
        _k8s.SetGet("fixed", null, new V1StorageClass { Metadata = new V1ObjectMeta { Name = "fixed" }, AllowVolumeExpansion = false });
        var ag = CreateAg();
        ag.Spec.Replicas = new AgReplicaTopologySpec { Count = 2, SynchronousCount = 1 };
        Pvc("data-ag-0", "12Gi", storageClass: "fixed");
        var replaced = Pvc("data-ag-1", "8Gi", storageClass: "fixed");

        var blocked = await _sut.ReconcileAsync(ag, [], "pw", CancellationToken.None);

        Assert.Contains("data-ag-1", blocked);
        Assert.Equal("8Gi", Requested(replaced));
    }

    [Fact]
    public async Task FullLogVolume_IsGrownByHalf()
    {
        // The 4 GB log volume of the test AG had 9 MB left; the log could not
        // grow any more and every write failed with 9002.
        var log = Pvc("log-ag-0", "4Gi");
        var data = Pvc("data-ag-0", "8Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        var blocked = await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Null(blocked);
        Assert.Equal("6Gi", Requested(log));
        Assert.Equal("8Gi", Requested(data));
        Assert.Single(_k8s.PatchCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "VolumeExpanded" && c.Message.Contains("log-ag-0 from 4Gi to 6Gi"));
    }

    [Fact]
    public async Task VolumesGrownInOnePass_AreReportedInOneEvent()
    {
        // Events of one reason replace each other when their message changes: on a test cluster only
        // the last of four expansions stayed visible.
        Pvc("log-ag-0", "4Gi");
        Pvc("data-ag-0", "8Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9, dataTotalMb: 7963, dataFreeMb: 550);

        await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        var expanded = Assert.Single(_eventPublisher.Calls, c => c.Reason == "VolumeExpanded");
        Assert.Contains("data-ag-0 from 8Gi to 12Gi", expanded.Message);
        Assert.Contains("log-ag-0 from 4Gi to 6Gi", expanded.Message);
        Assert.Contains("ceiling 16Gi", expanded.Message);
    }

    [Fact]
    public async Task BackupVolumeThatCouldNotBeWritten_IsGrown()
    {
        // No database file lives on the backup volume, so its usage cannot be read
        // beforehand; a failed write to it is what tells.
        var backup = Pvc("backup-ag-0", "10Gi");

        var outcome = await _sut.GrowBackupVolumeAsync(CreateAg(), "ag-0", CancellationToken.None);

        Assert.Equal("15Gi", Requested(backup));
        Assert.Contains("backup-ag-0 from 10Gi to 15Gi", outcome);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "VolumeExpanded");
    }

    [Fact]
    public async Task VolumeBelowTheThreshold_IsLeftAlone()
    {
        Pvc("log-ag-0", "4Gi");
        Volumes(logTotalMb: 4000, logFreeMb: 1000);

        await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
    }

    [Fact]
    public async Task ResizeStillInProgress_IsNotStacked()
    {
        // The filesystem has not caught up with the last request yet: SQL Server still sees it full.
        Pvc("log-ag-0", "6Gi", capacity: "4Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
    }

    [Fact]
    public async Task Growth_StopsAtTheCeiling_ThenReportsItCannotGrow()
    {
        var log = Pvc("log-ag-0", "12Gi");
        Volumes(logTotalMb: 12000, logFreeMb: 100);
        var ag = CreateAg(new AgVolumeAutoGrowSpec { LogMaxSize = "16Gi" });

        var first = await _sut.ReconcileAsync(ag, ["ag-0"], "pw", CancellationToken.None);

        Assert.Null(first);
        Assert.Equal("16Gi", Requested(log));

        _k8s.PatchCalls.Clear();
        Pvc("log-ag-0", "16Gi");
        Volumes(logTotalMb: 16000, logFreeMb: 100);

        var blocked = await _sut.ReconcileAsync(ag, ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
        Assert.Contains("ceiling", blocked);
        Assert.Contains("logMaxSize", blocked);
    }

    [Fact]
    public async Task DefaultCeiling_IsFourTimesTheDeclaredSize()
    {
        Pvc("log-ag-0", "16Gi");
        Volumes(logTotalMb: 16000, logFreeMb: 100);

        var blocked = await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
        Assert.Contains("ceiling", blocked);
    }

    [Fact]
    public async Task StorageClassWithoutExpansion_IsReported()
    {
        _k8s.SetGet("fixed", null, new V1StorageClass { Metadata = new V1ObjectMeta { Name = "fixed" }, AllowVolumeExpansion = false });
        Pvc("log-ag-0", "4Gi", storageClass: "fixed");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        var blocked = await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
        Assert.Contains("does not allow volume expansion", blocked);
    }

    [Fact]
    public async Task Disabled_NeverTouchesAVolume()
    {
        Pvc("log-ag-0", "4Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        await _sut.ReconcileAsync(CreateAg(new AgVolumeAutoGrowSpec { Mode = "Disabled" }), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
        Assert.Empty(_connFactory.ProbeCalls);
    }

    [Fact]
    public async Task UnreachableReplica_IsSkipped_OthersStillGrow()
    {
        Pvc("log-ag-1", "4Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);
        _connFactory.ConnectionForHost = h => h.StartsWith("ag-0.", StringComparison.Ordinal)
            ? throw new TimeoutException("unreachable")
            : new Microsoft.Data.SqlClient.SqlConnection($"Server={h}");

        await _sut.ReconcileAsync(CreateAg(), ["ag-0", "ag-1"], "pw", CancellationToken.None);

        Assert.Single(_k8s.PatchCalls);
    }
}
