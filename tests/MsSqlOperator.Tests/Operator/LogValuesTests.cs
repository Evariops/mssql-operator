using MsSqlOperator.Operator;

namespace MsSqlOperator.Tests.Operator;

public class LogValuesTests
{
    [Theory]
    [InlineData("system:serviceaccount:ns:sa", "system:serviceaccount:ns:sa")]
    [InlineData("alice\nINFO forged line", "alice INFO forged line")]
    [InlineData("alice\r\nINFO forged line", "alice INFO forged line")]
    [InlineData("alice\rINFO forged line", "alice INFO forged line")]
    [InlineData("alice\u2028INFO forged line", "alice INFO forged line")]
    [InlineData("", "")]
    public void OneLine_ReplacesEveryLineBreakWithASpace(string value, string expected)
    {
        Assert.Equal(expected, LogValues.OneLine(value));
    }

    [Fact]
    public void OneLine_KeepsNull()
    {
        Assert.Null(LogValues.OneLine(null));
    }
}
