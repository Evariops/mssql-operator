using FluentAssertions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Tests.Services;

public class TtlParserTests
{
    [Theory]
    [InlineData("30m", 30 * 60)]
    [InlineData("1h", 3600)]
    [InlineData("72h", 72 * 3600)]
    [InlineData("7d", 7 * 24 * 3600)]
    [InlineData("365d", 365 * 24 * 3600)]
    public void Parse_ValidTtl_ReturnsDuration(string ttl, int expectedSeconds)
    {
        var (valid, duration, error) = TtlParser.Parse(ttl);
        valid.Should().BeTrue();
        duration.TotalSeconds.Should().Be(expectedSeconds);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0h")]
    [InlineData("-5m")]
    [InlineData("1x")]
    [InlineData("1d12h")]
    [InlineData("h")]
    public void Parse_InvalidTtl_ReturnsFalse(string ttl)
    {
        var (valid, _, error) = TtlParser.Parse(ttl);
        valid.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
