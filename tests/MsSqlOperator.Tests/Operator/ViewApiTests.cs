using System.Text.Json;
using MsSqlOperator.Operator.ViewApi;

namespace MsSqlOperator.Tests.Operator;

public class ViewApiTests
{
    [Fact]
    public void ApiResourceList_SerializesToCamelCaseWithResources()
    {
        var list = new ApiResourceList
        {
            GroupVersion = "view.mssql-operator.evariops.com/v1alpha1",
            Resources =
            [
                new ApiResource { Name = "tdedatabasestates", SingularName = "tdedatabasestate", Kind = "TdeDatabaseState" },
            ],
        };

        var json = ViewApiJson.Serialize(list);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("APIResourceList", root.GetProperty("kind").GetString());
        Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
        Assert.Equal("view.mssql-operator.evariops.com/v1alpha1", root.GetProperty("groupVersion").GetString());
        var res = root.GetProperty("resources")[0];
        Assert.Equal("tdedatabasestates", res.GetProperty("name").GetString());
        Assert.Equal("TdeDatabaseState", res.GetProperty("kind").GetString());
        Assert.True(res.GetProperty("namespaced").GetBoolean());
        var verbs = res.GetProperty("verbs").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("get", verbs);
        Assert.Contains("list", verbs);
    }

    [Fact]
    public void ApiGroupList_AdvertisesTheGroup()
    {
        var groups = new ApiGroupList
        {
            Groups =
            [
                new ApiGroup
                {
                    Name = "view.mssql-operator.evariops.com",
                    Versions = [new GroupVersionForDiscovery { GroupVersion = "view.mssql-operator.evariops.com/v1alpha1", Version = "v1alpha1" }],
                },
            ],
        };

        var json = ViewApiJson.Serialize(groups);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("view.mssql-operator.evariops.com", doc.RootElement.GetProperty("groups")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Table_OmitsNullsAndKeepsColumnsAndRows()
    {
        var table = new Table
        {
            ColumnDefinitions = [new TableColumnDefinition { Name = "Database" }, new TableColumnDefinition { Name = "Encrypted", Type = "boolean" }],
            Rows = [new TableRow { Cells = ["AppDB", true] }],
        };

        var json = ViewApiJson.Serialize(table);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Table", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("columnDefinitions").GetArrayLength());
        Assert.Equal("AppDB", root.GetProperty("rows")[0].GetProperty("cells")[0].GetString());
        Assert.True(root.GetProperty("rows")[0].GetProperty("cells")[1].GetBoolean());
    }

    [Fact]
    public void TableRow_WithObject_SerializesPartialObjectMetadataForNamespaceColumn()
    {
        var table = new Table
        {
            ColumnDefinitions = [new TableColumnDefinition { Name = "Name" }],
            Rows =
            [
                new TableRow
                {
                    Cells = ["app-tde.sql1.appdb"],
                    Object = new Dictionary<string, object?>
                    {
                        ["kind"] = "PartialObjectMetadata",
                        ["apiVersion"] = "meta.k8s.io/v1",
                        ["metadata"] = new Dictionary<string, object?> { ["name"] = "app-tde.sql1.appdb", ["namespace"] = "mssql-test" },
                    },
                },
            ],
        };

        var json = ViewApiJson.Serialize(table);
        using var doc = JsonDocument.Parse(json);
        var obj = doc.RootElement.GetProperty("rows")[0].GetProperty("object");
        Assert.Equal("PartialObjectMetadata", obj.GetProperty("kind").GetString());
        Assert.Equal("mssql-test", obj.GetProperty("metadata").GetProperty("namespace").GetString());
    }

    [Fact]
    public async Task TdeProvider_ReadsEveryDatabaseStateInOneQuery()
    {
        // One read of the server-wide DMV per target, not one query per database.
        var k8s = new MsSqlOperator.Tests.Fakes.FakeKubernetesClient();
        k8s.SetList<MsSqlOperator.Entities.V1Alpha1.SqlTdeCertificate>(
        [
            new()
            {
                Metadata = new k8s.Models.V1ObjectMeta { Name = "cert", NamespaceProperty = "ns" },
                Spec = new MsSqlOperator.Entities.V1Alpha1.SqlTdeCertificateSpec
                {
                    Databases = new MsSqlOperator.Entities.V1Alpha1.TdeDatabaseScopeSpec
                    {
                        Selector = new MsSqlOperator.Entities.V1Alpha1.DatabaseSelectorSpec { AllUserDatabases = true },
                    },
                },
            },
        ]);
        var resolver = new MsSqlOperator.Tests.Fakes.FakeTargetResolver
        {
            Resolved = [new("SqlInstance", "inst", "ns", "inst.ns.svc.cluster.local", 1433, "pw", null)],
        };
        var tde = new MsSqlOperator.Tests.Fakes.FakeSqlTdeManager
        {
            UserDatabases = ["a", "b"],
            EncryptionStates = new(StringComparer.OrdinalIgnoreCase) { ["a"] = "Encrypted" },
        };
        var provider = new TdeDatabaseStatesProvider(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TdeDatabaseStatesProvider>.Instance,
            k8s, resolver, new MsSqlOperator.Tests.Fakes.FakeSqlConnectionFactory(), tde);

        var items = await provider.ListAsync("ns", null, CancellationToken.None);

        Assert.Equal(["Encrypted", "Unknown"], items.Select(i => i["encryptionState"]));
        Assert.Equal([true, false], items.Select(i => i["encrypted"]));
    }

    [Fact]
    public void TdeProvider_ToRow_MapsCellsInColumnOrder()
    {
        var provider = new TdeDatabaseStatesProvider(null!, null!, null!, null!, null!);
        var item = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "cert.inst.appdb" },
            ["certificate"] = "cert",
            ["target"] = "inst",
            ["database"] = "appdb",
            ["encrypted"] = true,
            ["encryptionState"] = "Encrypted",
        };

        var row = provider.ToRow(item);

        Assert.Equal("tdedatabasestates", provider.Resource.Name);
        Assert.Equal(new object?[] { "cert.inst.appdb", "cert", "inst", "appdb", true, "Encrypted" }, row.Cells);
    }

    [Fact]
    public void StoredProcProvider_ToRow_MapsCellsInColumnOrder()
    {
        var provider = new StoredProcedureDeploymentsProvider(null!, null!, null!, null!, null!, null!, null!);
        var item = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "sp.inst.appdb.dbo.usp_ping" },
            ["resource"] = "sp",
            ["target"] = "inst",
            ["database"] = "appdb",
            ["procedure"] = "dbo.usp_Ping",
            ["deployed"] = true,
        };

        var row = provider.ToRow(item);

        Assert.Equal("storedproceduredeployments", provider.Resource.Name);
        Assert.Equal(new object?[] { "sp.inst.appdb.dbo.usp_ping", "sp", "inst", "appdb", "dbo.usp_Ping", true }, row.Cells);
    }

    [Fact]
    public void SqlUserTargetsProvider_ToRow_JoinsRolesAndMapsCells()
    {
        var provider = new SqlUserTargetsProvider(null!, null!, null!, null!, null!, null!, null!);
        var item = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "u.inst.appdb" },
            ["login"] = "svc_app",
            ["target"] = "inst",
            ["scope"] = "DatabaseUser",
            ["database"] = "appdb",
            ["exists"] = true,
            ["enabled"] = null,
            ["roles"] = new List<string> { "db_datareader", "db_datawriter" },
        };

        var row = provider.ToRow(item);

        Assert.Equal("sqlusertargets", provider.Resource.Name);
        Assert.Equal(new object?[] { "u.inst.appdb", "svc_app", "inst", "DatabaseUser", "appdb", true, null, "db_datareader,db_datawriter" }, row.Cells);
    }

    [Fact]
    public void SqlAgentJobRunsProvider_ToRow_FormatsDatesAndMapsCells()
    {
        var provider = new SqlAgentJobRunsProvider(null!, null!, null!, null!, null!);
        var item = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "job.inst" },
            ["job"] = "Nightly",
            ["target"] = "inst",
            ["exists"] = true,
            ["lastRun"] = new DateTime(2026, 6, 1),
            ["lastStatus"] = "Succeeded",
            ["nextRun"] = new DateTime(2026, 6, 2),
        };

        var row = provider.ToRow(item);

        Assert.Equal("sqlagentjobruns", provider.Resource.Name);
        Assert.Equal(new object?[] { "job.inst", "Nightly", "inst", true, "2026-06-01", "Succeeded", "2026-06-02" }, row.Cells);
    }

    [Fact]
    public void AgReplicaStatesProvider_ToRow_MapsCellsInColumnOrder()
    {
        var provider = new AgReplicaStatesProvider(null!, null!, null!, null!);
        var item = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "ag.ag-0" },
            ["group"] = "ag",
            ["replica"] = "ag-0",
            ["role"] = "PRIMARY",
            ["syncHealth"] = "HEALTHY",
            ["connectionState"] = "CONNECTED",
            ["operationalState"] = "ONLINE",
            ["failoverReady"] = true,
            ["availabilityMode"] = "SYNCHRONOUS_COMMIT",
            ["allowConnections"] = "ALL",
        };

        var row = provider.ToRow(item);

        Assert.Equal("agreplicastates", provider.Resource.Name);
        Assert.Equal(new object?[] { "ag.ag-0", "ag", "ag-0", "PRIMARY", "HEALTHY", "CONNECTED", "ONLINE", true, "SYNCHRONOUS_COMMIT", "ALL" }, row.Cells);
    }

    [Fact]
    public async Task LogChainsProvider_ListsEveryReplicasChain_FlagsTheOperatorsFiles()
    {
        // Msdb is not replicated — each replica knows the backups it took.
        var k8s = new MsSqlOperator.Tests.Fakes.FakeKubernetesClient();
        k8s.SetList<MsSqlOperator.Entities.V1Alpha1.SqlAvailabilityGroupDatabase>(
        [
            new()
            {
                Metadata = new k8s.Models.V1ObjectMeta { Name = "orders", NamespaceProperty = "db" },
                Spec = new MsSqlOperator.Entities.V1Alpha1.SqlAvailabilityGroupDatabaseSpec
                {
                    AvailabilityGroupRef = new MsSqlOperator.Entities.Common.ResourceReference { Name = "sales" },
                    DatabaseName = "Orders",
                },
            },
        ]);
        k8s.SetGet("sales", "db", new MsSqlOperator.Entities.V1Alpha1.SqlAvailabilityGroup
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "sales", NamespaceProperty = "db" },
            Spec = new MsSqlOperator.Entities.V1Alpha1.SqlAvailabilityGroupSpec
            {
                Replicas = new MsSqlOperator.Entities.V1Alpha1.AgReplicaTopologySpec { Count = 2 },
                SaPasswordSecretRef = new MsSqlOperator.Entities.Common.SecretReference { Name = "sa", Key = "password" },
            },
        });
        k8s.SetGet("sa", "db", new k8s.Models.V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = [1, 2, 3] } });
        var agManager = new MsSqlOperator.Tests.Fakes.FakeSqlAgManager
        {
            BackupChain = [new MsSqlOperator.Services.SqlServer.BackupChainEntry("L", new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc),
                "100", "200", "/var/opt/mssql/backup/mssql-operator/Orders_20261028010000.trn")],
        };
        var provider = new LogChainsProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<LogChainsProvider>.Instance, k8s,
            new MsSqlOperator.Tests.Fakes.FakeSqlConnectionFactory(), agManager);

        var items = await provider.ListAsync("db", null, CancellationToken.None);

        Assert.Equal(["sales-0", "sales-1"], items.Select(i => i["replica"]).Order());
        Assert.All(items, i => Assert.Equal(true, i["byOperator"]));
        Assert.All(items, i => Assert.Equal("Log", i["type"]));
    }
}
