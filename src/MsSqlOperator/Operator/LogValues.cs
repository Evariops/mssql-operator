namespace MsSqlOperator.Operator;

/// <summary>
/// Keeps a value that comes from a request (a forwarded identity, a route value, a webhook path) to one
/// line before it is logged. In the text log format, a line break inside such a value would start a
/// forged log line.
/// </summary>
internal static class LogValues
{
    /// <summary>The value with every line break (CR, LF, CRLF and the Unicode line separators) replaced
    /// by a space. Null stays null.</summary>
    public static string? OneLine(string? value) => value?.ReplaceLineEndings(" ");
}
