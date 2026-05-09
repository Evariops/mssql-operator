namespace MsSqlOperator.Configuration;

public static class SqlServerImages
{
    private static readonly Dictionary<(string Version, string Edition), string> ImageMap = new(new VersionEditionComparer())
    {
        { ("2019", "Developer"),  "mcr.microsoft.com/mssql/server:2019-latest" },
        { ("2019", "Express"),    "mcr.microsoft.com/mssql/server:2019-latest" },
        { ("2019", "Standard"),   "mcr.microsoft.com/mssql/server:2019-latest" },
        { ("2019", "Enterprise"), "mcr.microsoft.com/mssql/server:2019-latest" },
        { ("2022", "Developer"),  "mcr.microsoft.com/mssql/server:2022-latest" },
        { ("2022", "Express"),    "mcr.microsoft.com/mssql/server:2022-latest" },
        { ("2022", "Standard"),   "mcr.microsoft.com/mssql/server:2022-latest" },
        { ("2022", "Enterprise"), "mcr.microsoft.com/mssql/server:2022-latest" },
        { ("2025", "Developer"),  "mcr.microsoft.com/mssql/server:2025-latest" },
        { ("2025", "Express"),    "mcr.microsoft.com/mssql/server:2025-latest" },
        { ("2025", "Standard"),   "mcr.microsoft.com/mssql/server:2025-latest" },
        { ("2025", "Enterprise"), "mcr.microsoft.com/mssql/server:2025-latest" },
    };

    public static readonly string[] SupportedVersions = ["2019", "2022", "2025"];
    public static readonly string[] SupportedEditions = ["Developer", "Express", "Standard", "Enterprise"];
    public static readonly string[] AgEditions = ["Developer", "Enterprise"];

    public static string GetImage(string version, string edition, string? customImage = null, string? customTag = null)
    {
        if (customImage != null)
        {
            return customTag != null ? $"{customImage}:{customTag}" : customImage;
        }

        if (ImageMap.TryGetValue((version, edition), out var image))
        {
            return customTag != null ? image.Replace(image.Split(':').Last(), customTag) : image;
        }

        return $"mcr.microsoft.com/mssql/server:{version}-latest";
    }

    public static bool IsValidCombination(string version, string edition) => ImageMap.ContainsKey((version, edition));

    public static bool IsAgCompatible(string edition) => AgEditions.Contains(edition, StringComparer.OrdinalIgnoreCase);

    public static bool IsValidUpgrade(string fromVersion, string toVersion)
    {
        var fromIdx = Array.IndexOf(SupportedVersions, fromVersion);
        var toIdx = Array.IndexOf(SupportedVersions, toVersion);
        return fromIdx >= 0 && toIdx >= 0 && toIdx > fromIdx;
    }

    private class VersionEditionComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(obj.Item1.ToLowerInvariant(), obj.Item2.ToLowerInvariant());
    }
}
