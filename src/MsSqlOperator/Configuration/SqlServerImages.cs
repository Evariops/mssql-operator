namespace MsSqlOperator.Configuration;

public static class SqlServerImages
{
    private static readonly Dictionary<(string Version, string Edition), string> ImageMap = new(new VersionEditionComparer())
    {
        // Explicit CU tags, never floating "-latest". CU upgrades are unidirectional
        // for AG replicas (an upgraded replica cannot fall back to secondary of an older
        // primary), so an unnoticed image drift mid-rolling-update can wedge an AG.
        // Renovate keeps these current (customManagers regex on this file).
        { ("2019", "Developer"),  "mcr.microsoft.com/mssql/server:2019-CU32-ubuntu-20.04" },
        { ("2019", "Express"),    "mcr.microsoft.com/mssql/server:2019-CU32-ubuntu-20.04" },
        { ("2019", "Standard"),   "mcr.microsoft.com/mssql/server:2019-CU32-ubuntu-20.04" },
        { ("2019", "Enterprise"), "mcr.microsoft.com/mssql/server:2019-CU32-ubuntu-20.04" },
        { ("2022", "Developer"),  "mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04" },
        { ("2022", "Express"),    "mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04" },
        { ("2022", "Standard"),   "mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04" },
        { ("2022", "Enterprise"), "mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04" },
        { ("2025", "Developer"),  "mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04" },
        { ("2025", "Express"),    "mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04" },
        { ("2025", "Standard"),   "mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04" },
        { ("2025", "Enterprise"), "mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04" },
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

        // Never fabricate a floating tag for an unknown combination — that silently
        // bypassed IsValidCombination and deployed an unpinned image.
        throw new ArgumentException($"Unsupported SQL Server version/edition combination: {version}/{edition}.");
    }

    public static bool IsValidCombination(string version, string edition) => ImageMap.ContainsKey((version, edition));

    public static bool IsAgCompatible(string edition) => AgEditions.Contains(edition, StringComparer.OrdinalIgnoreCase);

    public static bool IsValidUpgrade(string fromVersion, string toVersion)
    {
        var fromIdx = Array.IndexOf(SupportedVersions, fromVersion);
        var toIdx = Array.IndexOf(SupportedVersions, toVersion);
        return fromIdx >= 0 && toIdx >= 0 && toIdx > fromIdx;
    }

    /// <summary>
    /// The SQL Server major version an image reference actually deploys, or null when it
    /// cannot be determined.
    ///
    /// <c>spec.version</c> was the only thing validated against downgrades — but it does not decide
    /// what runs: <c>image</c>/<c>imageTag</c> override it wholesale. So "2022" with
    /// <c>imageTag: 2019-CU32-ubuntu-20.04</c> passed admission and quietly deployed SQL Server 2019
    /// onto databases upgraded to the 2022 format, which the older engine cannot even open. The
    /// version is now read from the tag itself and checked against the declared one.
    /// </summary>
    public static string? MajorVersionFromImage(string? image, string? imageTag)
    {
        var candidate = imageTag ?? (image?.Contains(':') == true ? image.Split(':').Last() : null);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        foreach (var version in SupportedVersions)
        {
            if (candidate.StartsWith(version, StringComparison.Ordinal))
                return version;
        }
        return null;
    }

    /// <summary>Rejection message when the image/tag deploys a different major version than
    /// <paramref name="declaredVersion"/>, or null.</summary>
    public static string? CheckImageMatchesVersion(string declaredVersion, string? image, string? imageTag)
    {
        var fromImage = MajorVersionFromImage(image, imageTag);
        if (fromImage is null || fromImage == declaredVersion)
            return null;

        return $"the image tag deploys SQL Server {fromImage} while spec version is {declaredVersion}. " +
               "The engine version is what determines the database format: a mismatch either strands the databases " +
               "on a build that cannot open them, or upgrades them irreversibly without anyone deciding to. " +
               "Align the version with the image.";
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
