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
        Assert.Equal(expectedMb, MemoryCalculator.ParseToMb(input));
    }

    [Theory]
    [InlineData("1073741824", 1024)] // 1 GB in bytes
    [InlineData("2147483648", 2048)] // 2 GB in bytes
    public void ParseToMb_PlainBytes_ReturnsCorrectMb(string input, int expectedMb)
    {
        Assert.Equal(expectedMb, MemoryCalculator.ParseToMb(input));
    }

    // Two distinct budgets. The PROCESS limit (mssql.conf memorylimitmb,
    // MSSQL_MEMORY_LIMIT_MB) is total - reserve; sp_configure 'max server memory' sits at 90% of it,
    // leaving head-room for what SQL Server allocates OUTSIDE that budget — thread stacks, fdhost and
    // the full-text crawl buffers. Equal values left zero head-room inside the cgroup and the kernel
    // OOM-killed the container during the full-text storm at startup.

    [Fact]
    public void CalculateProcessMemoryLimitMb_DefaultReserve_SubtractsReserve()
    {
        var spec = new MemorySpec { Total = "8Gi" }; // default reserve = 2Gi
        Assert.Equal(6144, MemoryCalculator.CalculateProcessMemoryLimitMb(spec)); // 8192 - 2048
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_LeavesHeadroomBelowTheProcessLimit()
    {
        var spec = new MemorySpec { Total = "8Gi" }; // default reserve = 2Gi
        var processLimit = MemoryCalculator.CalculateProcessMemoryLimitMb(spec);
        var maxServerMemory = MemoryCalculator.CalculateSqlMaxMemoryMb(spec);

        Assert.Equal(5529, maxServerMemory); // 90% of 6144
        Assert.True(maxServerMemory < processLimit);
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_CustomReserve_SubtractsReserve()
    {
        var spec = new MemorySpec { Total = "16Gi", SystemReserve = "4Gi" };
        Assert.Equal(12288, MemoryCalculator.CalculateProcessMemoryLimitMb(spec)); // 16384 - 4096
        Assert.Equal(11059, MemoryCalculator.CalculateSqlMaxMemoryMb(spec));       // 90% of 12288
    }

    [Fact]
    public void CalculateSqlMinMemoryMb_NoMinMemory_ReturnsZero()
    {
        var spec = new MemorySpec { Total = "8Gi" };
        Assert.Equal(0, MemoryCalculator.CalculateSqlMinMemoryMb(spec));
    }

    [Fact]
    public void CalculateSqlMinMemoryMb_WithMinMemory_ReturnsParsedValue()
    {
        var spec = new MemorySpec { Total = "8Gi", MinMemory = "2Gi" };
        Assert.Equal(2048, MemoryCalculator.CalculateSqlMinMemoryMb(spec));
    }

    // ── Kata overhead tests ─────────────────────────────────

    [Fact]
    public void CalculateSqlMaxMemoryMb_WithKataRuntime_SubtractsOverhead()
    {
        var spec = new MemorySpec { Total = "32Gi", SystemReserve = "2Gi" };
        // process limit: 32768 - 2048 - 150 = 30570; max server memory: 90% of it
        Assert.Equal(30570, MemoryCalculator.CalculateProcessMemoryLimitMb(spec, "kata"));
        Assert.Equal(27513, MemoryCalculator.CalculateSqlMaxMemoryMb(spec, "kata"));
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_WithoutKataRuntime_NoOverhead()
    {
        var spec = new MemorySpec { Total = "32Gi", SystemReserve = "2Gi" };
        // process limit: 32768 - 2048 = 30720; max server memory: 90% of it
        Assert.Equal(30720, MemoryCalculator.CalculateProcessMemoryLimitMb(spec));
        Assert.Equal(27648, MemoryCalculator.CalculateSqlMaxMemoryMb(spec));
    }

    [Fact]
    public void CalculateSqlMaxMemoryMb_WithNullRuntime_NoOverhead()
    {
        var spec = new MemorySpec { Total = "8Gi" };
        Assert.Equal(6144, MemoryCalculator.CalculateProcessMemoryLimitMb(spec, null)); // 8192 - 2048
        Assert.Equal(5529, MemoryCalculator.CalculateSqlMaxMemoryMb(spec, null));       // 90% of it
    }

    [Theory]
    [InlineData("kata", true)]
    [InlineData("kata-qemu", true)]
    [InlineData("kata-clh", true)]
    [InlineData("KATA", true)]
    [InlineData("runc", false)]
    [InlineData(null, false)]
    [InlineData("gvisor", false)]
    public void IsKataRuntime_DetectsCorrectly(string? runtimeClassName, bool expected)
    {
        Assert.Equal(expected, MemoryCalculator.IsKataRuntime(runtimeClassName));
    }
}
