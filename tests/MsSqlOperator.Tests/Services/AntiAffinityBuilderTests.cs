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

    [Fact]
    public void Build_PoolAntiAffinityRequired_AddsRequiredHostnameTermOnPoolLabel()
    {
        var result = _builder.Build(null, new Dictionary<string, string> { ["app"] = "mssql" },
            pool: "prod-critical", poolAntiAffinity: "Required");

        result.Should().NotBeNull();
        var term = result!.PodAntiAffinity!.RequiredDuringSchedulingIgnoredDuringExecution.Should().ContainSingle().Subject;
        term.TopologyKey.Should().Be("kubernetes.io/hostname");
        term.LabelSelector.MatchLabels.Should().ContainKey("mssql-operator.evariops.com/pool")
            .WhoseValue.Should().Be("prod-critical");
    }

    [Fact]
    public void Build_PoolAntiAffinityPreferred_AddsPreferredTerm()
    {
        var result = _builder.Build(null, new Dictionary<string, string> { ["app"] = "mssql" },
            pool: "prod-critical", poolAntiAffinity: "Preferred");

        result!.PodAntiAffinity!.PreferredDuringSchedulingIgnoredDuringExecution.Should().ContainSingle();
        result.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Build_PoolAntiAffinity_WithoutPool_Ignored()
    {
        var result = _builder.Build(null, new Dictionary<string, string> { ["app"] = "mssql" },
            pool: null, poolAntiAffinity: "Required");

        result.Should().BeNull();
    }
}
