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
        Assert.Null(_builder.Build(null, null, Identity));
    }

    [Fact]
    public void Build_EmptySpec_ReturnsNull()
    {
        Assert.Null(_builder.Build(new TopologySpreadSpec(), null, Identity));
    }

    [Fact]
    public void Build_SelfNodeRequired_EmitsDoNotScheduleHostnameOnIdentity()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } };

        var c = _builder.Build(spec, null, Identity);

        Assert.Single(c!);
        Assert.Equal("kubernetes.io/hostname", c![0].TopologyKey);
        Assert.Equal("DoNotSchedule", c[0].WhenUnsatisfiable);
        Assert.Equal(1, c[0].MaxSkew);
        // Self-spread selector is the resource identity only (stable across upgrades).
        Assert.Contains("mssql-operator.evariops.com/ag", c[0].LabelSelector.MatchLabels);
        Assert.DoesNotContain("mssql-operator.evariops.com/version", c[0].LabelSelector.MatchLabels);
    }

    [Fact]
    public void Build_SelfZonePreferred_EmitsScheduleAnywayZone()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred" } };

        var c = _builder.Build(spec, null, Identity);

        Assert.Single(c!);
        Assert.Equal("topology.kubernetes.io/zone", c![0].TopologyKey);
        Assert.Equal("ScheduleAnyway", c[0].WhenUnsatisfiable);
    }

    [Fact]
    public void Build_SelfNodeAndZone_EmitsTwoConstraints()
    {
        var spec = new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", Zone = "Preferred", MaxSkew = 2 } };

        var c = _builder.Build(spec, null, Identity);

        Assert.Equal(2, c!.Count());
        Assert.Equivalent(new[] { "kubernetes.io/hostname", "topology.kubernetes.io/zone" }, c!.Select(x => x.TopologyKey));
        Assert.All(c!, x => Assert.Equal(2, x.MaxSkew));
    }

    [Fact]
    public void Build_MinDomains_OnlyAppliedToRequiredRule()
    {
        var required = _builder.Build(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", MinDomains = 3 } }, null, Identity);
        Assert.Equal(3, required![0].MinDomains);

        var preferred = _builder.Build(new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred", MinDomains = 3 } }, null, Identity);
        Assert.Null(preferred![0].MinDomains);
    }

    [Fact]
    public void Build_PoolRule_UsesPoolLabelSelector()
    {
        var spec = new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } };

        var c = _builder.Build(spec, "prod-critical", Identity);

        Assert.Single(c!);
        Assert.Equal("prod-critical", c![0].LabelSelector.MatchLabels["mssql-operator.evariops.com/pool"]);
    }

    [Fact]
    public void Build_PoolRule_WithoutPool_SkipsConstraint()
    {
        var spec = new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } };

        Assert.Null(_builder.Build(spec, null, Identity));
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

        Assert.Equal(2, c!.Count());
        Assert.Equal("topology.kubernetes.io/region", c!.Last().TopologyKey);
    }
}
