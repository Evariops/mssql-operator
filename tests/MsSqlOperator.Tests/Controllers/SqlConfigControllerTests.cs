using System.Text;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

/// <summary>What a SqlConfiguration puts back, and on which server.</summary>
public class SqlConfigControllerTests
{
    private const string MaxMemory = "max server memory (MB)";
    private const string MaxDop = "max degree of parallelism";

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlServerManager _sqlServer = new();
    private readonly SqlConfigController _controller;

    public SqlConfigControllerTests()
    {
        // The Database Mail manager is only used when spec.databaseMail is set (never here).
        _controller = new SqlConfigController(NullLogger<SqlConfigController>.Instance, _k8s, _connFactory, _sqlServer,
            null!, new OperatorMetrics(), new FakeEventPublisher());
        _k8s.SetGet("sa", "default", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("pw") } });
    }

    private static readonly SecretReference Sa = new() { Name = "sa", Key = "password" };

    private static SqlConfiguration Config(SpConfigureSpec spConfigure, params OriginalSettingStatus[] originals) => new()
    {
        Metadata = new V1ObjectMeta { Name = "cfg", NamespaceProperty = "default", Generation = 1 },
        Spec = new SqlConfigurationSpec { SpConfigure = spConfigure },
        Status = new SqlConfigurationStatus { OriginalSettings = originals.Length == 0 ? null : [.. originals] },
    };

    private static SqlInstance Instance(string name, bool selected, MemorySpec? memory = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default" },
        Spec = new SqlInstanceSpec
        {
            SaPasswordSecretRef = Sa,
            Memory = memory,
            ConfigurationRef = selected ? new ResourceReference { Name = "cfg" } : null,
        },
    };

    [Fact]
    public async Task RecreatedConfiguration_EmptyStatus_PutsBackWhatItsSqlRecordsSay()
    {
        // GitOps recreated the configuration (new UID, empty status). What it changed is
        // recorded in the msdb of the server: the flag it turned on goes off, the value it changed
        // returns.
        var ownership = new FakeOwnershipRegistry();
        var former = new MsSqlOperator.Services.SqlServer.ResourceOwner("SqlConfiguration", "default", "cfg", "uid-old");
        var server = _connFactory.ConnectionForHost(MsSqlOperator.Services.Kubernetes.SqlEndpoints.InstancePodFqdn("sql", "default")).DataSource;
        ownership.TraceFlags[(server, 1222)] = former;
        ownership.Settings[(server, MaxDop)] = new MsSqlOperator.Services.SqlServer.OwnedSetting(MaxDop, 0, former);
        _k8s.SetList<SqlInstance>([Instance("sql", selected: true)]);
        _sqlServer.ActiveTraceFlags = [1222];
        _sqlServer.Configured[MaxDop] = 8;
        var controller = new SqlConfigController(NullLogger<SqlConfigController>.Instance, _k8s, _connFactory, _sqlServer,
            null!, new OperatorMetrics(), new FakeEventPublisher(), ownership: ownership);
        var config = Config(new SpConfigureSpec());
        config.Metadata.Uid = "uid-new";

        await controller.ReconcileAsync(config, CancellationToken.None);

        Assert.Equal([1222], _sqlServer.DisableTraceFlagCalls);
        Assert.Contains((MaxDop, 0), _sqlServer.SpConfigureCalls);
        Assert.Empty(ownership.TraceFlags);
        Assert.Empty(ownership.Settings);
    }

    [Fact]
    public async Task OriginalsOnlyInTheStatus_AreMigratedToSql()
    {
        // A ledger written by an earlier version lived in the status only.
        var ownership = new FakeOwnershipRegistry();
        _k8s.SetList<SqlInstance>([Instance("sql", selected: true)]);
        _sqlServer.Configured[MaxDop] = 4;
        var controller = new SqlConfigController(NullLogger<SqlConfigController>.Instance, _k8s, _connFactory, _sqlServer,
            null!, new OperatorMetrics(), new FakeEventPublisher(), ownership: ownership);
        var config = Config(new SpConfigureSpec { MaxDegreeOfParallelism = 4 },
            new OriginalSettingStatus { Instance = "sql", Option = MaxDop, Value = 0 });
        config.Metadata.Uid = "uid-1";

        await controller.ReconcileAsync(config, CancellationToken.None);

        Assert.Equal(0, Assert.Single(ownership.Settings).Value.OriginalValue);
    }

    [Fact]
    public async Task OptionNowSetBySpecMemory_IsNotPutBack()
    {
        // The configuration had set max server memory (its original is in the ledger); the instance
        // then got spec.memory. Restoring the original overwrote the instance's own alignment.
        _k8s.SetList<SqlInstance>([Instance("sql", selected: true, new MemorySpec { Total = "16Gi" })]);
        _sqlServer.Configured[MaxMemory] = 14336;
        _sqlServer.Configured[MaxDop] = 0;
        var config = Config(new SpConfigureSpec { MaxServerMemoryMb = 8192, MaxDegreeOfParallelism = 4 },
            new OriginalSettingStatus { Instance = "sql", Option = MaxMemory, Value = 2147483647 });

        await _controller.ReconcileAsync(config, CancellationToken.None);

        Assert.DoesNotContain(_sqlServer.SpConfigureCalls, c => c.Name == MaxMemory);
        Assert.Contains((MaxDop, 4), _sqlServer.SpConfigureCalls);
        Assert.DoesNotContain(config.Status!.OriginalSettings ?? [], o => o.Option == MaxMemory);
    }

    [Fact]
    public async Task ValueArmedByADba_NothingIsCommitted_AndItIsSaid()
    {
        // With something of its own to commit, the operator's RECONFIGURE also committed the
        // value a DBA had armed to commit at a quiet time.
        _k8s.SetList<SqlInstance>([Instance("sql", selected: true)]);
        _sqlServer.Configured[MaxDop] = 0;
        _sqlServer.PendingByOthers = ["cost threshold for parallelism"];
        var config = Config(new SpConfigureSpec { MaxDegreeOfParallelism = 4 });

        var result = await _controller.ReconcileAsync(config, CancellationToken.None);

        Assert.Empty(_sqlServer.SpConfigureCalls);
        var blocked = result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReconfigureBlocked");
        Assert.Equal("True", blocked.Status);
        Assert.Contains("sql: cost threshold for parallelism", blocked.Message);
        Assert.Equal("False", result.Entity.Status.Conditions!.Single(c => c.Type == "Applied").Status);

        _sqlServer.PendingByOthers = null;
        result = await _controller.ReconcileAsync(result.Entity, CancellationToken.None);

        Assert.Contains((MaxDop, 4), _sqlServer.SpConfigureCalls);
        Assert.Equal("False", result.Entity!.Status!.Conditions!.Single(c => c.Type == "ReconfigureBlocked").Status);
    }

    [Fact]
    public async Task ReplicaNamedLikeASqlInstance_KeepsItsOwnLedgerEntry()
    {
        // AG "sales" has a pod "sales-0"; a SqlInstance "sales-0" can live next to it. Keyed by pod
        // name, indexing the targets threw on the duplicate, and the two servers shared their entry.
        _k8s.SetList<SqlInstance>([Instance("sales-0", selected: false)]);
        _k8s.SetList<SqlAvailabilityGroup>([new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "default" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Replicas = new AgReplicaTopologySpec { Count = 3 },
                SaPasswordSecretRef = Sa,
                InstanceTemplate = new AgInstanceTemplateSpec { ConfigurationRef = new ResourceReference { Name = "cfg" } },
            },
        }]);
        _sqlServer.Configured[MaxDop] = 0;
        // The SqlInstance was selected before: its original MAXDOP is owed back to it.
        var config = Config(new SpConfigureSpec { MaxDegreeOfParallelism = 4 },
            new OriginalSettingStatus { Instance = "sales-0", Option = MaxDop, Value = 8 });

        var result = await _controller.ReconcileAsync(config, CancellationToken.None);

        Assert.True(result.IsSuccess);
        // The replicas record their own originals; the SqlInstance got its value back and left the ledger.
        Assert.Equal(["sales/sales-0", "sales/sales-1", "sales/sales-2"],
            config.Status!.OriginalSettings!.Select(o => o.Instance).ToList());
        Assert.Contains(_connFactory.Calls, c => c.Host == "sales-0-0.sales-0.default.svc.cluster.local");
        Assert.Contains((MaxDop, 8), _sqlServer.SpConfigureCalls);
    }

    [Fact]
    public void LegacyEntryUnderAReplicaPodName_MovesToTheReplicaKey()
    {
        var config = new SqlConfiguration
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string>
                {
                    [ConfigurationLedger.LegacyTraceFlagsAnnotationPrefix + "sales-0"] = "1222",
                    [ConfigurationLedger.LegacyTraceFlagsAnnotationPrefix + "solo"] = "3226",
                }
            },
        };
        var targets = new List<ConfigTarget>
        {
            new("sales", "sales-0", "h0", Sa, false, false),
            new("solo", "solo", "h1", Sa, false, false),
        };

        var ledger = ConfigurationLedger.Load(config);
        ledger.AdoptReplicaKeys(targets);

        Assert.Equal(["sales/sales-0", "solo"], ledger.Instances.Order().ToList());
        Assert.Equal([1222], ledger.TraceFlags("sales/sales-0"));
    }
}
