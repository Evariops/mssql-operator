using System.Text.Json;
using FluentAssertions;
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

        root.GetProperty("kind").GetString().Should().Be("APIResourceList");
        root.GetProperty("apiVersion").GetString().Should().Be("v1");
        root.GetProperty("groupVersion").GetString().Should().Be("view.mssql-operator.evariops.com/v1alpha1");
        var res = root.GetProperty("resources")[0];
        res.GetProperty("name").GetString().Should().Be("tdedatabasestates");
        res.GetProperty("kind").GetString().Should().Be("TdeDatabaseState");
        res.GetProperty("namespaced").GetBoolean().Should().BeTrue();
        res.GetProperty("verbs").EnumerateArray().Select(v => v.GetString()).Should().Contain(["get", "list"]);
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
        doc.RootElement.GetProperty("groups")[0].GetProperty("name").GetString().Should().Be("view.mssql-operator.evariops.com");
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
        root.GetProperty("kind").GetString().Should().Be("Table");
        root.GetProperty("columnDefinitions").GetArrayLength().Should().Be(2);
        root.GetProperty("rows")[0].GetProperty("cells")[0].GetString().Should().Be("AppDB");
        root.GetProperty("rows")[0].GetProperty("cells")[1].GetBoolean().Should().BeTrue();
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
        obj.GetProperty("kind").GetString().Should().Be("PartialObjectMetadata");
        obj.GetProperty("metadata").GetProperty("namespace").GetString().Should().Be("mssql-test");
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

        provider.Resource.Name.Should().Be("tdedatabasestates");
        row.Cells.Should().Equal("cert.inst.appdb", "cert", "inst", "appdb", true, "Encrypted");
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

        provider.Resource.Name.Should().Be("storedproceduredeployments");
        row.Cells.Should().Equal("sp.inst.appdb.dbo.usp_ping", "sp", "inst", "appdb", "dbo.usp_Ping", true);
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

        provider.Resource.Name.Should().Be("sqlusertargets");
        row.Cells.Should().Equal("u.inst.appdb", "svc_app", "inst", "DatabaseUser", "appdb", true, null, "db_datareader,db_datawriter");
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

        provider.Resource.Name.Should().Be("sqlagentjobruns");
        row.Cells.Should().Equal("job.inst", "Nightly", "inst", true, "2026-06-01", "Succeeded", "2026-06-02");
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

        provider.Resource.Name.Should().Be("agreplicastates");
        row.Cells.Should().Equal("ag.ag-0", "ag", "ag-0", "PRIMARY", "HEALTHY", "CONNECTED", "ONLINE", true, "SYNCHRONOUS_COMMIT", "ALL");
    }
}
