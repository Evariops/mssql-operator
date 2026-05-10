using FluentAssertions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Tests.Services;

public class MemoryCalculatorTests
{
    [Theory]
    [InlineData("1Gi", 1024)]
    [InlineData("2Gi", 2048)]
    [InlineData("8Gi", 8192)]
    [InlineData("16Gi", 16384)]
    [InlineData("512Mi", 512)]
    [InlineData("1024Mi", 1024)]
    [InlineData("1Ti", 1048576)]
    [InlineData("1Ki", 0)] // 1 Ki = 1/1024 MB, truncates to 0
    [InlineData("4096Ki", 4)] // 4096 Ki = 4 MB
    public void ParseToMb_ValidSuffixes_ReturnsCorrectMb(string input, int expectedMb)
    {
        MemoryCalculator.ParseToMb(input).Should().Be(expectedMb);
    }

    [Theory]
    [InlineData("1073741824", 1024)] // 1 GB in bytes
    [InlineData("2147483648", 2048)] // 2 GB in bytes
    public void ParseToMb_PlainBytes_ReturnsCorrectMb(string input, int expectedMb)
    {
        MemoryCalculator.ParseToMb(input).Should().Be(expectedMb);
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_DefaultReserve_SubtractsReserve()
    {
        var spec = new MemorySpec { Total = "8Gi" }; // default reserve = 2Gi
        var result = MemoryCalculator.CalculateSqlMaxMemoryMb(spec);
        result.Should().Be(6144); // 8192 - 2048
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_CustomReserve_SubtractsReserve()
    {
        var spec = new MemorySpec { Total = "16Gi", SystemReserve = "4Gi" };
        var result = MemoryCalculator.CalculateSqlMaxMemoryMb(spec);
        result.Should().Be(12288); // 16384 - 4096
    }

    [Fact]
    public void CalculateSqlMinMemoryMb_NoMinMemory_ReturnsZero()
    {
        var spec = new MemorySpec { Total = "8Gi" };
        MemoryCalculator.CalculateSqlMinMemoryMb(spec).Should().Be(0);
    }

    [Fact]
    public void CalculateSqlMinMemoryMb_WithMinMemory_ReturnsParsedValue()
    {
        var spec = new MemorySpec { Total = "8Gi", MinMemory = "2Gi" };
        MemoryCalculator.CalculateSqlMinMemoryMb(spec).Should().Be(2048);
    }
}
