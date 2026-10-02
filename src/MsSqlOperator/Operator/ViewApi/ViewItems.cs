namespace MsSqlOperator.Operator.ViewApi;

/// <summary>Item helpers shared by the view providers (each carried its own copy).</summary>
public static class ViewItems
{
    /// <summary>The item's <c>metadata.name</c>, or "".</summary>
    public static string Name(Dictionary<string, object?> item) =>
        item["metadata"] is Dictionary<string, object?> m && m.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

    /// <summary>A lowercase name made of letters, digits, '-' and '.' only.</summary>
    public static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
}
