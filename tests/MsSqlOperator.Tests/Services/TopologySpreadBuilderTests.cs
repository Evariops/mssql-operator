using FluentAssertions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class TopologySpreadBuilderTests
{
    private readonly TopologySpreadBuilder _builder = new();
    private static readonly Dictionary<string, string> Identity =
        new() { ["mssql-operator.evariops.com/ag"] = "my-ag" };

    [Fact]
    public void Build_NullSpec_ReturnsNull()
    {
        _builder.Build(null, null, Identity).Should().BeNull();
    }

    [Fact]
    public void Build_EmptySpec_ReturnsNull()
    {
        _builder.Build(new TopologySpreadSpec(), null, Identity).Should().BeNull();
    }

    [Fact]
    public void Build_SelfNodeRequired_EmitsDoNotScheduleHostnameOnIdentity()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } };

        var c = _builder.Build(spec, null, Identity);

        c.Should().ContainSingle();
        c![0].TopologyKey.Should().Be("kubernetes.io/hostname");
        c[0].WhenUnsatisfiable.Should().Be("DoNotSchedule");
        c[0].MaxSkew.Should().Be(1);
        // Self-spread selector is the resource identity only (stable across upgrades).
        c[0].LabelSelector.MatchLabels.Should().ContainKey("mssql-operator.evariops.com/ag")
            .And.NotContainKey("mssql-operator.evariops.com/version");
    }

    [Fact]
    public void Build_SelfZonePreferred_EmitsScheduleAnywayZone()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred" } };

        var c = _builder.Build(spec, null, Identity);

        c.Should().ContainSingle();
        c![0].TopologyKey.Should().Be("topology.kubernetes.io/zone");
        c[0].WhenUnsatisfiable.Should().Be("ScheduleAnyway");
    }

    [Fact]
    public void Build_SelfNodeAndZone_EmitsTwoConstraints()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", Zone = "Preferred", MaxSkew = 2 } };

        var c = _builder.Build(spec, null, Identity);

        c.Should().HaveCount(2);
        c!.Select(x => x.TopologyKey).Should().BeEquivalentTo(["kubernetes.io/hostname", "topology.kubernetes.io/zone"]);
        c.Should().OnlyContain(x => x.MaxSkew == 2);
    }

    [Fact]
    public void Build_MinDomains_OnlyAppliedToRequiredRule()
    {
        var required = _builder.Build(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", MinDomains = 3 } }, null, Identity);
        required![0].MinDomains.Should().Be(3);

        var preferred = _builder.Build(new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred", MinDomains = 3 } }, null, Identity);
        preferred![0].MinDomains.Should().BeNull();
    }

    [Fact]
    public void Build_PoolRule_UsesPoolLabelSelector()
    {
        var spec = new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } };

        var c = _builder.Build(spec, "prod-critical", Identity);

        c.Should().ContainSingle();
        c![0].LabelSelector.MatchLabels.Should().ContainKey("mssql-operator.evariops.com/pool")
            .WhoseValue.Should().Be("prod-critical");
    }

    [Fact]
    public void Build_PoolRule_WithoutPool_SkipsConstraint()
    {
        var spec = new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } };

        _builder.Build(spec, null, Identity).Should().BeNull();
    }

    [Fact]
    public void Build_RawConstraints_AppendedVerbatim()
    {
        var spec = new TopologySpreadSpec
        {
            Self = new SpreadRule { Node = "Required" },
            Constraints =
            [
                new k8s.Models.V1TopologySpreadConstraint
                {
                    TopologyKey = "topology.kubernetes.io/region",
                    MaxSkew = 1,
                    WhenUnsatisfiable = "ScheduleAnyway"
                }
            ]
        };

        var c = _builder.Build(spec, null, Identity);

        c.Should().HaveCount(2);
        c!.Last().TopologyKey.Should().Be("topology.kubernetes.io/region");
    }
}
