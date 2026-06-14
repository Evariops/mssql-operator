using FluentAssertions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class TopologySpreadValidatorTests
{
    private static string? Validate(TopologySpreadSpec? spread, string? pool = null, bool singlePod = false, AntiAffinitySpec? aa = null)
        => TopologySpreadValidator.Validate(spread, aa, pool, singlePod, []);

    [Fact]
    public void NullSpread_NoPool_Ok() => Validate(null).Should().BeNull();

    [Fact]
    public void ValidSelfSpread_Ok() =>
        Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", Zone = "Preferred" } }).Should().BeNull();

    [Fact]
    public void InvalidMode_Fails() =>
        Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Sometimes" } })!.Should().Contain("must be 'Required' or 'Preferred'");

    [Fact]
    public void MaxSkewZero_Fails() =>
        Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", MaxSkew = 0 } })!.Should().Contain("maxSkew must be >= 1");

    [Fact]
    public void MinDomainsWithoutRequired_Fails() =>
        Validate(new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred", MinDomains = 2 } })!.Should().Contain("minDomains only applies");

    [Fact]
    public void PoolRuleWithoutPool_Fails() =>
        Validate(new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } })!.Should().Contain("requires spec.pool");

    [Fact]
    public void PoolRuleWithPool_Ok() =>
        Validate(new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } }, pool: "p1").Should().BeNull();

    [Fact]
    public void PoolAntiAffinityWithoutPool_Fails() =>
        Validate(new TopologySpreadSpec { PoolAntiAffinity = "Required" })!.Should().Contain("requires spec.pool");

    [Fact]
    public void InvalidPoolName_Fails() =>
        Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } }, pool: "Bad Pool!")!.Should().Contain("RFC1123");

    [Fact]
    public void RawConstraint_InvalidWhenUnsatisfiable_Fails()
    {
        var spread = new TopologySpreadSpec
        {
            Constraints = [new k8s.Models.V1TopologySpreadConstraint { TopologyKey = "x", MaxSkew = 1, WhenUnsatisfiable = "Maybe" }]
        };
        Validate(spread)!.Should().Contain("whenUnsatisfiable");
    }

    [Fact]
    public void SinglePodSelfSpread_WarnsButValid()
    {
        var warnings = new List<string>();
        var err = TopologySpreadValidator.Validate(
            new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } }, null, null, isSinglePod: true, warnings);

        err.Should().BeNull();
        warnings.Should().ContainSingle().Which.Should().Contain("single-pod");
    }

    [Fact]
    public void HostnameOverlap_WarnsButValid()
    {
        var warnings = new List<string>();
        var err = TopologySpreadValidator.Validate(
            new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } },
            new AntiAffinitySpec { RequiredSelfAntiAffinity = new SelfAntiAffinityRule { TopologyKey = "kubernetes.io/hostname" } },
            null, isSinglePod: false, warnings);

        err.Should().BeNull();
        warnings.Should().Contain(w => w.Contains("both target the hostname"));
    }
}
