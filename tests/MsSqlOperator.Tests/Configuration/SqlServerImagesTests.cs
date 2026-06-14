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
    public void IsValidUpgrade_UpgradingVersion_ReturnsTrue(string from, string to)
    {
        Assert.True(SqlServerImages.IsValidUpgrade(from, to));
    }

    [Theory]
    [InlineData("2022", "2019")]
    [InlineData("2025", "2019")]
    public void IsValidUpgrade_DowngradingVersion_ReturnsFalse(string from, string to)
    {
        Assert.False(SqlServerImages.IsValidUpgrade(from, to));
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
}
