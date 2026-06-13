using FluentAssertions;
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

        gvk.Group.Should().Be("mssql-operator.evariops.com");
        gvk.Version.Should().Be("v1alpha1");
        gvk.Kind.Should().Be("SqlInstance");
        gvk.PluralName.Should().Be("sqlinstances");
        gvk.ApiVersion.Should().Be("mssql-operator.evariops.com/v1alpha1");
        gvk.IsCoreGroup.Should().BeFalse();
    }

    [Fact]
    public void Of_CoreResource_HasEmptyGroupAndPlainApiVersion()
    {
        var gvk = GroupVersionKind.Of<V1Secret>();

        gvk.Group.Should().BeEmpty();
        gvk.Version.Should().Be("v1");
        gvk.ApiVersion.Should().Be("v1");
        gvk.IsCoreGroup.Should().BeTrue();
    }
}
