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
    /// an empty list, or a <c>*</c> entry, allows everything (configuration binding appends to the
    /// default list, so <c>*</c> is the only way to lift the restriction from Helm).</summary>
    public static bool IsAllowed(string? image, IReadOnlyCollection<string> registries)
        => image is null
           || registries.Count == 0
           || registries.Any(r => r == "*" || image.StartsWith(r.EndsWith('/') ? r : r + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal message for <paramref name="image"/>, or null when it is allowed.</summary>
    public static string? Refusal(string? image, IReadOnlyCollection<string> registries, string path)
        => IsAllowed(image, registries)
            ? null
            : $"{path}.image \"{image}\" is not in an allowed registry ({string.Join(", ", registries)}); " +
              "the operator's allowedImageRegistries setting lists them.";
}
