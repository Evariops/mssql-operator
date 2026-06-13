using FluentAssertions;
using MsSqlOperator.Configuration;

namespace MsSqlOperator.Tests.Configuration;

/// <summary>
/// The sp_configure policy classifier is the single source of truth shared by the admission
/// validator and the reconciler. These tests pin the three tiers so the reconcile-time
/// gate (which prevents enabling xp_cmdshell/clr/etc. without an explicit allowDangerous opt-in even
/// when the webhook is off) and the admission gate can never diverge.
/// </summary>
public class SpConfigureAllowlistTests
{
    [Theory]
    [InlineData("max degree of parallelism")]
    [InlineData("max server memory (MB)")]
    [InlineData("cost threshold for parallelism")]
    public void Classify_SafeOption_ReturnsSafe(string name) =>
        SpConfigureAllowlist.Classify(name).Should().Be(SpConfigureAllowlist.Tier.Safe);

    [Theory]
    [InlineData("xp_cmdshell")]
    [InlineData("clr enabled")]
    [InlineData("Ole Automation Procedures")]
    [InlineData("external scripts enabled")]
    [InlineData("Ad Hoc Distributed Queries")]
    public void Classify_DangerousOption_ReturnsDangerous(string name) =>
        SpConfigureAllowlist.Classify(name).Should().Be(SpConfigureAllowlist.Tier.Dangerous);

    [Theory]
    [InlineData("totally made up option")]
    [InlineData("xp_cmdshell ")]            // trailing space: not an exact allowlist match
    [InlineData("")]
    public void Classify_UnknownOption_ReturnsUnknown(string name) =>
        SpConfigureAllowlist.Classify(name).Should().Be(SpConfigureAllowlist.Tier.Unknown);

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        SpConfigureAllowlist.Classify("XP_CMDSHELL").Should().Be(SpConfigureAllowlist.Tier.Dangerous);
        SpConfigureAllowlist.Classify("MAX DEGREE OF PARALLELISM").Should().Be(SpConfigureAllowlist.Tier.Safe);
    }

    [Fact]
    public void Safe_And_Dangerous_Sets_DoNotOverlap()
    {
        // A name that is both "safe" and "dangerous" would make the tier order-dependent.
        SpConfigureAllowlist.Safe.Overlaps(SpConfigureAllowlist.Dangerous).Should().BeFalse();
    }
}
