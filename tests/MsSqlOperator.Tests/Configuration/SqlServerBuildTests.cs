using MsSqlOperator.Configuration;

namespace MsSqlOperator.Tests.Configuration;

public class SqlServerBuildTests
{
    [Theory]
    [InlineData("2022-CU25-ubuntu-22.04", 2022, 25, 0, "ubuntu-22.04")]
    [InlineData("2019-CU32-ubuntu-20.04", 2019, 32, 0, "ubuntu-20.04")]
    [InlineData("2025-RTM-ubuntu-22.04", 2025, 0, 0, "ubuntu-22.04")]
    [InlineData("2019-CU32-GDR1-ubuntu-20.04", 2019, 32, 1, "ubuntu-20.04")]
    public void TryParseTag_PinnedTag_ParsesEveryComponent(string tag, int major, int cu, int gdr, string baseOs)
    {
        Assert.True(SqlServerBuild.TryParseTag(tag, out var build));
        Assert.Equal(new SqlServerBuild(major, cu, gdr, baseOs), build);
        Assert.Equal(tag, build.Tag);
    }

    [Theory]
    [InlineData("2022-latest")]      // floating: the whole point of pinning
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("my-own-tag")]
    [InlineData("2022-CUX-ubuntu-22.04")]
    [InlineData("2019-GDR-ubuntu-20.04")]   // a GDR with no CU baseline: not a shape we claim to read
    public void TryParseTag_NotAPinnedBuild_Fails(string tag)
    {
        Assert.False(SqlServerBuild.TryParseTag(tag, out _));
    }

    [Theory]
    [InlineData("RTM", 0, 0)]
    [InlineData("CU25", 25, 0)]
    [InlineData("CU32-GDR1", 32, 1)]
    public void TryParsePatchLevel_ValidLevel_Parses(string patchLevel, int cu, int gdr)
    {
        Assert.True(SqlServerBuild.TryParsePatchLevel(patchLevel, out var parsedCu, out var parsedGdr));
        Assert.Equal(cu, parsedCu);
        Assert.Equal(gdr, parsedGdr);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("CU")]
    [InlineData("25")]
    [InlineData("2022-CU25-ubuntu-22.04")]   // a full tag is not a patch level
    public void TryParsePatchLevel_Invalid_Fails(string patchLevel)
    {
        Assert.False(SqlServerBuild.TryParsePatchLevel(patchLevel, out _, out _));
    }

    [Fact]
    public void CompareTo_OrdersByMajorThenCuThenGdr()
    {
        var rtm2022 = SqlServerBuild.ParseTag("2022-RTM-ubuntu-22.04");
        var cu24 = SqlServerBuild.ParseTag("2022-CU24-ubuntu-22.04");
        var cu24Gdr1 = SqlServerBuild.ParseTag("2022-CU24-GDR1-ubuntu-22.04");
        var cu25 = SqlServerBuild.ParseTag("2022-CU25-ubuntu-22.04");
        var rtm2025 = SqlServerBuild.ParseTag("2025-RTM-ubuntu-22.04");

        Assert.True(rtm2022 < cu24);
        Assert.True(cu24 < cu24Gdr1);
        Assert.True(cu24Gdr1 < cu25);
        Assert.True(cu25 < rtm2025);   // a newer major outranks any CU of the older one
    }

    [Fact]
    public void CompareTo_IgnoresTheOsBase()
    {
        // The OS base is not a version: 2022-CU25 on 20.04 and on 22.04 are the same engine build,
        // so neither may be treated as an upgrade over the other.
        var ubuntu2004 = new SqlServerBuild(2022, 25, 0, "ubuntu-20.04");
        var ubuntu2204 = new SqlServerBuild(2022, 25, 0, "ubuntu-22.04");

        Assert.Equal(0, ubuntu2004.CompareTo(ubuntu2204));
    }

    [Fact]
    public void WithPatchLevel_KeepsMajorAndOsBase()
    {
        var build = SqlServerBuild.ParseTag("2022-CU25-ubuntu-22.04").WithPatchLevel(19);

        Assert.Equal("2022-CU19-ubuntu-22.04", build.Tag);
    }

    [Theory]
    [InlineData("2022-latest", true)]
    [InlineData("latest", true)]
    [InlineData(null, true)]                      // untagged: Docker's implicit :latest
    [InlineData("2022-CU25-ubuntu-22.04", false)]
    public void IsFloating_DetectsUnpinnedTags(string? tag, bool floating)
    {
        Assert.Equal(floating, SqlServerBuild.IsFloating(tag));
    }
}
