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
        Assert.Single(_k8s.UpdateCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "VolumeExpanded" && c.Message.Contains("log-ag-0"));
    }

    [Fact]
    public async Task VolumeBelowTheThreshold_IsLeftAlone()
    {
        Pvc("log-ag-0", "4Gi");
        Volumes(logTotalMb: 4000, logFreeMb: 1000);

        await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
    }

    [Fact]
    public async Task ResizeStillInProgress_IsNotStacked()
    {
        // The filesystem has not caught up with the last request yet: SQL Server still sees it full.
        Pvc("log-ag-0", "6Gi", capacity: "4Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
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

        _k8s.UpdateCalls.Clear();
        Pvc("log-ag-0", "16Gi");
        Volumes(logTotalMb: 16000, logFreeMb: 100);

        var blocked = await _sut.ReconcileAsync(ag, ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
        Assert.Contains("ceiling", blocked);
        Assert.Contains("logMaxSize", blocked);
    }

    [Fact]
    public async Task DefaultCeiling_IsFourTimesTheDeclaredSize()
    {
        Pvc("log-ag-0", "16Gi");
        Volumes(logTotalMb: 16000, logFreeMb: 100);

        var blocked = await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
        Assert.Contains("ceiling", blocked);
    }

    [Fact]
    public async Task StorageClassWithoutExpansion_IsReported()
    {
        _k8s.SetGet("fixed", null, new V1StorageClass { Metadata = new V1ObjectMeta { Name = "fixed" }, AllowVolumeExpansion = false });
        Pvc("log-ag-0", "4Gi", storageClass: "fixed");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        var blocked = await _sut.ReconcileAsync(CreateAg(), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
        Assert.Contains("does not allow volume expansion", blocked);
    }

    [Fact]
    public async Task Disabled_NeverTouchesAVolume()
    {
        Pvc("log-ag-0", "4Gi");
        Volumes(logTotalMb: 3951, logFreeMb: 9);

        await _sut.ReconcileAsync(CreateAg(new AgVolumeAutoGrowSpec { Mode = "Disabled" }), ["ag-0"], "pw", CancellationToken.None);

        Assert.Empty(_k8s.UpdateCalls);
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

        Assert.Single(_k8s.UpdateCalls);
    }
}
