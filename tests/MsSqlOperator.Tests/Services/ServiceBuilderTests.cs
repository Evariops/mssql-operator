using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class ServiceBuilderTests
{
    private readonly ServiceBuilder _builder = new();

    [Fact]
    public void Build_ClusterIP_CreatesCorrectService()
    {
        var labels = new Dictionary<string, string> { { "app", "mssql" } };
        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var spec = new ServiceSpec { Type = "ClusterIP", Port = 1433 };

        var svc = _builder.Build("my-svc", "default", labels, spec, owner);

        Assert.Equal("my-svc", svc.Metadata.Name);
        Assert.Equal("default", svc.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", svc.Spec.Type);
        Assert.Single(svc.Spec.Ports, p => p.Port == 1433);
        Assert.Single(svc.Metadata.OwnerReferences, o => o.Name == "test");
    }

    [Fact]
    public void Build_LoadBalancer_SetsCorrectType()
    {
        var spec = new ServiceSpec { Type = "LoadBalancer", Port = 1433 };
        var svc = _builder.Build("lb-svc", "ns", new Dictionary<string, string>(), spec, null);

        Assert.Equal("LoadBalancer", svc.Spec.Type);
    }

    [Fact]
    public void BuildHeadless_CreatesClusterIPNone()
    {
        var labels = new Dictionary<string, string> { { "app", "mssql" } };
        var svc = _builder.BuildHeadless("headless-svc", "default", labels, 1433, null);

        Assert.Equal("None", svc.Spec.ClusterIP);
        Assert.Single(svc.Spec.Ports, p => p.Port == 1433);
    }
}
