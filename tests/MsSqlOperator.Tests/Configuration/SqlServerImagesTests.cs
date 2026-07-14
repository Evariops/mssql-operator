using MsSqlOperator.Configuration;

namespace MsSqlOperator.Tests.Configuration;

public class SqlServerImagesTests
{
    [Theory]
    [InlineData("2022", "Developer")]
    [InlineData("2022", "Enterprise")]
    [InlineData("2022", "Standard")]
    [InlineData("2019", "Developer")]
    [InlineData("2019", "Enterprise")]
    [InlineData("2025", "Developer")]
    public void IsValidCombination_ValidVersionEdition_ReturnsTrue(string version, string edition)
    {
        Assert.True(SqlServerImages.IsValidCombination(version, edition));
    }

    [Theory]
    [InlineData("2099", "Developer")]
    [InlineData("2022", "InvalidEdition")]
    public void IsValidCombination_InvalidVersionEdition_ReturnsFalse(string version, string edition)
    {
        Assert.False(SqlServerImages.IsValidCombination(version, edition));
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Developer")]
    public void IsAgCompatible_EnterpriseDeveloper_ReturnsTrue(string edition)
    {
        Assert.True(SqlServerImages.IsAgCompatible(edition));
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Express")]
    [InlineData("Web")]
    public void IsAgCompatible_NonAgEdition_ReturnsFalse(string edition)
    {
        Assert.False(SqlServerImages.IsAgCompatible(edition));
    }

    [Theory]
    [InlineData("2019", "2022")]
    [InlineData("2019", "2025")]
    [InlineData("2022", "2025")]
    public void CheckUpgrade_MajorVersionUpgrade_IsAccepted(string from, string to)
    {
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(from, "Developer"),
            SqlServerImages.Resolve(to, "Developer")));
    }

    [Theory]
    [InlineData("2022", "2019")]
    [InlineData("2025", "2019")]
    public void CheckUpgrade_MajorVersionDowngrade_IsRejected(string from, string to)
    {
        var error = SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(from, "Developer"),
            SqlServerImages.Resolve(to, "Developer"));

        Assert.NotNull(error);
        Assert.Contains($"Version downgrade from {from} to {to}", error);
    }

    [Theory]
    [InlineData("2022", "Developer")]
    [InlineData("2019", "Enterprise")]
    public void GetImage_ValidCombination_ReturnsImage(string version, string edition)
    {
        var image = SqlServerImages.GetImage(version, edition);
        Assert.False(string.IsNullOrEmpty(image));
        Assert.Contains("mcr.microsoft.com/mssql/server", image);
    }

    [Fact]
    public void Resolve_NoPin_UsesTheOperatorsPinnedDefault()
    {
        var resolution = SqlServerImages.Resolve("2022", "Developer");

        Assert.Equal(SqlServerImages.DefaultBuildFor("2022"), resolution.Build);
        Assert.False(resolution.IsFloating);
    }

    [Fact]
    public void Resolve_CumulativeUpdate_KeepsTheOsBaseOfThatVersion()
    {
        // The CU is the only thing the user pins; the repository and OS base come from the catalogue,
        // so "CU19" cannot silently become a tag that does not exist for this version.
        var resolution = SqlServerImages.Resolve("2022", "Developer", cumulativeUpdate: "CU19");

        Assert.Equal("mcr.microsoft.com/mssql/server:2022-CU19-ubuntu-22.04", resolution.Image);
        Assert.Equal("CU19", resolution.CumulativeUpdate);
        Assert.Equal(19, resolution.Build!.Value.CumulativeUpdate);
    }

    [Fact]
    public void Resolve_CustomRepositoryWithTagAndImageTag_DoesNotConcatenateTwoTags()
    {
        // The old GetImage produced "repo:mirror-tag:2022-CU25-..." — an unpullable reference.
        var resolution = SqlServerImages.Resolve("2022", "Developer",
            image: "registry.internal:5000/mirror/mssql:2022-CU24-ubuntu-22.04",
            imageTag: "2022-CU25-ubuntu-22.04");

        Assert.Equal("registry.internal:5000/mirror/mssql:2022-CU25-ubuntu-22.04", resolution.Image);
        Assert.Equal(25, resolution.Build!.Value.CumulativeUpdate);
    }

    [Fact]
    public void Resolve_DigestPinnedImage_IsLeftUntouched()
    {
        const string digest = "registry.internal/mssql@sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var resolution = SqlServerImages.Resolve("2022", "Developer", image: digest);

        Assert.Equal(digest, resolution.Image);
        Assert.Null(resolution.Build);
        Assert.False(resolution.IsFloating);   // a digest is the strongest pin there is
    }

    [Fact]
    public void CheckSelection_FloatingTag_IsRejected()
    {
        var error = SqlServerImages.CheckSelection("2022", "Developer", null, null, "2022-latest");

        Assert.NotNull(error);
        Assert.Contains("not pinned", error);
    }

    [Fact]
    public void CheckSelection_CumulativeUpdateAndImageTag_IsRejected()
    {
        var error = SqlServerImages.CheckSelection("2022", "Developer", "CU25", null, "2022-CU24-ubuntu-22.04");

        Assert.NotNull(error);
        Assert.Contains("set one", error);
    }

    [Fact]
    public void CheckSelection_ImageTagOfAnotherMajorVersion_IsRejected()
    {
        var error = SqlServerImages.CheckSelection("2022", "Developer", null, null, "2019-CU32-ubuntu-20.04");

        Assert.NotNull(error);
        Assert.Contains("deploys SQL Server 2019", error);
    }

    [Fact]
    public void CheckSelection_UnreadableCustomTag_IsAcceptedWithAWarning()
    {
        var warnings = new List<string>();
        var error = SqlServerImages.CheckSelection("2022", "Developer", null,
            "registry.internal/mssql", "internal-build-42", warnings);

        Assert.Null(error);
        Assert.Contains(warnings, w => w.Contains("guards cannot apply"));
    }

    [Fact]
    public void CheckSelection_PinnedCu_IsAccepted()
    {
        var warnings = new List<string>();

        Assert.Null(SqlServerImages.CheckSelection("2022", "Developer", "CU19", null, null, warnings));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("CU25", "CU19")]     // CU rollback: the databases are already upgraded
    [InlineData("CU25", "RTM")]
    public void CheckUpgrade_CuDowngrade_IsRejected(string from, string to)
    {
        var error = SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve("2022", "Developer", from),
            SqlServerImages.Resolve("2022", "Developer", to));

        Assert.NotNull(error);
        Assert.Contains("Cumulative update downgrade", error);
    }

    [Theory]
    [InlineData("CU19", "CU25")]
    [InlineData("RTM", "CU1")]
    [InlineData("CU25", "CU25")]
    public void CheckUpgrade_ForwardOrUnchanged_IsAccepted(string from, string to)
    {
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve("2022", "Developer", from),
            SqlServerImages.Resolve("2022", "Developer", to)));
    }

    [Fact]
    public void CheckUpgrade_UnreadableTag_IsNotJudged()
    {
        // A custom image the operator cannot read is the escape hatch: it cannot be compared, so it
        // must not be guessed at either way.
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve("2022", "Developer", null, "registry.internal/mssql", "build-42"),
            SqlServerImages.Resolve("2022", "Developer", null, "registry.internal/mssql", "build-41")));
    }

    [Fact]
    public void IsDowngradeApproved_OnlyOnTheExplicitAnnotation()
    {
        Assert.True(SqlServerImages.IsDowngradeApproved(
            new Dictionary<string, string> { [SqlServerImages.AllowVersionDowngradeAnnotation] = "true" }));
        Assert.False(SqlServerImages.IsDowngradeApproved(
            new Dictionary<string, string> { [SqlServerImages.AllowVersionDowngradeAnnotation] = "false" }));
        Assert.False(SqlServerImages.IsDowngradeApproved(null));
    }
}
