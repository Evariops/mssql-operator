namespace MsSqlOperator.Configuration;

/// <summary>
/// Where <c>spec.image</c> may come. Any registry used to be accepted: an author allowed
/// to create a SqlInstance — but not pods — got the operator to run an image of their choosing, with
/// the sa password Secret injected.
/// </summary>
public static class ImagePolicy
{
    /// <summary>True for the default image, or one under an allowed registry prefix. A prefix is
    /// matched as a path (<c>registry.example.com</c> never matches <c>registry.example.com.evil.io</c>);
    /// an empty list allows everything.</summary>
    public static bool IsAllowed(string? image, IReadOnlyCollection<string> registries)
        => image is null
           || registries.Count == 0
           || registries.Any(r => image.StartsWith(r.EndsWith('/') ? r : r + "/", StringComparison.OrdinalIgnoreCase));
}
