using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class AntiAffinityBuilderTests
{
    private readonly AntiAffinityBuilder _builder = new();

    [Fact]
    public void Build_RequiredSelfAntiAffinity_CreatesRequiredAntiAffinity()
    {
        var spec = new AntiAffinitySpec
        {
            RequiredSelfAntiAffinity = new SelfAntiAffinityRule
            {
                TopologyKey = "kubernetes.io/hostname"
            }
        };
        var labels = new Dictionary<string, string> { { "app", "mssql" } };

        var affinity = _builder.Build(spec, labels);

        affinity.Should().NotBeNull();
        affinity!.PodAntiAffinity.Should().NotBeNull();
        affinity.PodAntiAffinity!.RequiredDuringSchedulingIgnoredDuringExecution.Should().NotBeEmpty();
        var term = affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution![0];
        term.TopologyKey.Should().Be("kubernetes.io/hostname");
    }

    [Fact]
    public void Build_PreferredSelfAntiAffinity_CreatesPreferredAntiAffinity()
    {
        var spec = new AntiAffinitySpec
        {
            PreferredSelfAntiAffinity = new PreferredSelfAntiAffinityRule
            {
                TopologyKey = "topology.kubernetes.io/zone",
                Weight = 80
            }
        };
        var labels = new Dictionary<string, string> { { "app", "mssql" } };

        var affinity = _builder.Build(spec, labels);

        affinity.Should().NotBeNull();
        affinity!.PodAntiAffinity.Should().NotBeNull();
        affinity.PodAntiAffinity!.PreferredDuringSchedulingIgnoredDuringExecution.Should().NotBeEmpty();
    }

    [Fact]
    public void Build_NullSpec_ReturnsNull()
    {
        var result = _builder.Build(null, new Dictionary<string, string>());

        result.Should().BeNull();
    }
}
