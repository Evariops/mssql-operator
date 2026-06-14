using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Tests.Operator;

public class GroupVersionKindTests
{
    [Fact]
    public void Of_CustomResource_ResolvesGroupVersionPlural()
    {
        var gvk = GroupVersionKind.Of<SqlInstance>();

        Assert.Equal("mssql-operator.evariops.com", gvk.Group);
        Assert.Equal("v1alpha1", gvk.Version);
        Assert.Equal("SqlInstance", gvk.Kind);
        Assert.Equal("sqlinstances", gvk.PluralName);
        Assert.Equal("mssql-operator.evariops.com/v1alpha1", gvk.ApiVersion);
        Assert.False(gvk.IsCoreGroup);
    }

    [Fact]
    public void Of_CoreResource_HasEmptyGroupAndPlainApiVersion()
    {
        var gvk = GroupVersionKind.Of<V1Secret>();

        Assert.Empty(gvk.Group);
        Assert.Equal("v1", gvk.Version);
        Assert.Equal("v1", gvk.ApiVersion);
        Assert.True(gvk.IsCoreGroup);
    }
}
