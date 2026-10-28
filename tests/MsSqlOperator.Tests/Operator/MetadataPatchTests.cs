using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Tests.Operator;

/// <summary>Consuming an annotation patches the annotation and nothing else.</summary>
public class MetadataPatchTests
{
    [Fact]
    public void MetadataPatch_TestsTheResourceVersion_AndTouchesOnlyTheAnnotation()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta
            {
                Name = "ag", ResourceVersion = "42",
                Annotations = new Dictionary<string, string> { ["mssql-operator.evariops.com/failover-to"] = "ag-1", ["keep"] = "x" },
            },
        };

        var ops = KubernetesClientAdapter.MetadataPatch(ag, new Dictionary<string, string?>
        {
            ["mssql-operator.evariops.com/failover-to"] = null,
            ["mssql-operator.evariops.com/absent"] = null, // already gone: no remove that would fail the patch
            ["a~b"] = "v",
        });

        Assert.Equal(3, ops.Count);
        Assert.Equal(("test", "/metadata/resourceVersion", (object?)"42"), ((string)ops[0]["op"]!, (string)ops[0]["path"]!, ops[0]["value"]));
        Assert.Equal(("remove", "/metadata/annotations/mssql-operator.evariops.com~1failover-to"), ((string)ops[1]["op"]!, (string)ops[1]["path"]!));
        Assert.Equal(("add", "/metadata/annotations/a~0b"), ((string)ops[2]["op"]!, (string)ops[2]["path"]!));
        Assert.DoesNotContain(ops, o => ((string)o["path"]!).StartsWith("/spec", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataPatch_NoAnnotationsYet_AddsTheMap()
    {
        var ag = new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag", ResourceVersion = "1" } };

        var ops = KubernetesClientAdapter.MetadataPatch(ag, new Dictionary<string, string?> { ["k"] = "v" });

        Assert.Equal("/metadata/annotations", ops[1]["path"]);
    }
}
