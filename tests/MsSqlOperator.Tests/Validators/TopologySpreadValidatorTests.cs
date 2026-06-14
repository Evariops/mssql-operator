using MsSqlOperator.Entities.Common;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class TopologySpreadValidatorTests
{
    private static string? Validate(TopologySpreadSpec? spread, string? pool = null, bool singlePod = false, AntiAffinitySpec? aa = null)
        => TopologySpreadValidator.Validate(spread, aa, pool, singlePod, []);

    [Fact]
    public void NullSpread_NoPool_Ok() => Assert.Null(Validate(null));

    [Fact]
    public void ValidSelfSpread_Ok() =>
        Assert.Null(Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", Zone = "Preferred" } }));

    [Fact]
    public void InvalidMode_Fails() =>
        Assert.Contains("must be 'Required' or 'Preferred'", Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Sometimes" } })!);

    [Fact]
    public void MaxSkewZero_Fails() =>
        Assert.Contains("maxSkew must be >= 1", Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required", MaxSkew = 0 } })!);

    [Fact]
    public void MinDomainsWithoutRequired_Fails() =>
        Assert.Contains("minDomains only applies", Validate(new TopologySpreadSpec { Self = new SpreadRule { Zone = "Preferred", MinDomains = 2 } })!);

    [Fact]
    public void PoolRuleWithoutPool_Fails() =>
        Assert.Contains("requires spec.pool", Validate(new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } })!);

    [Fact]
    public void PoolRuleWithPool_Ok() =>
        Assert.Null(Validate(new TopologySpreadSpec { Pool = new SpreadRule { Node = "Required" } }, pool: "p1"));

    [Fact]
    public void PoolAntiAffinityWithoutPool_Fails() =>
        Assert.Contains("requires spec.pool", Validate(new TopologySpreadSpec { PoolAntiAffinity = "Required" })!);

    [Fact]
    public void InvalidPoolName_Fails() =>
        Assert.Contains("RFC1123", Validate(new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } }, pool: "Bad Pool!")!);

    [Fact]
    public void RawConstraint_InvalidWhenUnsatisfiable_Fails()
    {
        var spread = new TopologySpreadSpec
        {
            Constraints = [new k8s.Models.V1TopologySpreadConstraint { TopologyKey = "x", MaxSkew = 1, WhenUnsatisfiable = "Maybe" }]
        };
        Assert.Contains("whenUnsatisfiable", Validate(spread)!);
    }

    [Fact]
    public void SinglePodSelfSpread_WarnsButValid()
    {
        var warnings = new List<string>();
        var err = TopologySpreadValidator.Validate(
            new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } }, null, null, isSinglePod: true, warnings);

        Assert.Null(err);
        Assert.Contains("single-pod", Assert.Single(warnings));
    }

    [Fact]
    public void HostnameOverlap_WarnsButValid()
    {
        var warnings = new List<string>();
        var err = TopologySpreadValidator.Validate(
            new TopologySpreadSpec { Self = new SpreadRule { Node = "Required" } },
            new AntiAffinitySpec { RequiredSelfAntiAffinity = new SelfAntiAffinityRule { TopologyKey = "kubernetes.io/hostname" } },
            null, isSinglePod: false, warnings);

        Assert.Null(err);
        Assert.Contains(warnings, w => w.Contains("both target the hostname"));
    }
}
