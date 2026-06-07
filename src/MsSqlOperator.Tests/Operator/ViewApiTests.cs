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
            GroupVersion = "view.mssql.operator/v1alpha1",
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
        root.GetProperty("groupVersion").GetString().Should().Be("view.mssql.operator/v1alpha1");
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
                    Name = "view.mssql.operator",
                    Versions = [new GroupVersionForDiscovery { GroupVersion = "view.mssql.operator/v1alpha1", Version = "v1alpha1" }],
                },
            ],
        };

        var json = ViewApiJson.Serialize(groups);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("groups")[0].GetProperty("name").GetString().Should().Be("view.mssql.operator");
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
}
