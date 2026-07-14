using System.Globalization;

namespace MsSqlOperator.Configuration;

/// <summary>
/// The image an instance runs, resolved from what the spec declares and checked for the one property
/// SQL Server cannot recover from: a build that moves backwards.
///
/// <para><b>Resolution order</b> — <c>imageTag</c> ▸ <c>cumulativeUpdate</c> ▸ a tag baked into
/// <c>image</c> ▸ the operator's pinned default for <c>version</c>. Admission rejects the ambiguous
/// combinations (see <see cref="CheckSelection"/>), so in practice exactly one of them is in play.</para>
/// </summary>
public static class SqlServerImages
{
    public const string DefaultRepository = "mcr.microsoft.com/mssql/server";

    /// <summary>Approves a build that moves backwards (see <see cref="CheckUpgrade"/>). Rolling an
    /// instance back to an earlier CU is not something SQL Server supports in place — the databases
    /// have already been upgraded by the newer build — so it takes a deliberate annotation and a
    /// restore plan, not a silent edit of the tag.</summary>
    public const string AllowVersionDowngradeAnnotation = "mssql-operator.evariops.com/allow-version-downgrade";

    // Explicit CU tags, never floating "-latest". CU upgrades are unidirectional
    // for AG replicas (an upgraded replica cannot fall back to secondary of an older
    // primary), so an unnoticed image drift mid-rolling-update can wedge an AG.
    // Renovate keeps these current (customManagers regex on this file) — which is why they are
    // written out as full literal references rather than composed from DefaultRepository.
    private static readonly string[] PinnedImages =
    [
        "mcr.microsoft.com/mssql/server:2019-CU32-ubuntu-20.04",
        "mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04",
        "mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04",
    ];

    /// <summary>Major version → the build shipped as the default, and the OS base a requested
    /// <c>cumulativeUpdate</c> is published under. The edition is not in here on purpose: every
    /// edition runs the same image and is selected by MSSQL_PID at runtime.</summary>
    private static readonly Dictionary<int, SqlServerBuild> DefaultBuilds = PinnedImages
        .Select(reference => SqlServerBuild.ParseTag(reference[(reference.LastIndexOf(':') + 1)..]))
        .ToDictionary(build => build.Major);

    public static readonly string[] SupportedVersions = DefaultBuilds.Keys
        .Order()
        .Select(major => major.ToString(CultureInfo.InvariantCulture))
        .ToArray();

    public static readonly string[] SupportedEditions = ["Developer", "Express", "Standard", "Enterprise"];
    public static readonly string[] AgEditions = ["Developer", "Enterprise"];

    /// <summary>The pinned default build for a major version — what an instance runs when it pins
    /// no <c>cumulativeUpdate</c>, and the OS base every CU of that version is looked up under.</summary>
    public static SqlServerBuild DefaultBuildFor(string version) =>
        TryMajor(version, out var major) && DefaultBuilds.TryGetValue(major, out var build)
            ? build
            : throw new ArgumentException($"Unsupported SQL Server version: {version}.", nameof(version));

    public static bool IsValidCombination(string version, string edition) =>
        TryMajor(version, out var major)
        && DefaultBuilds.ContainsKey(major)
        && SupportedEditions.Contains(edition, StringComparer.OrdinalIgnoreCase);

    public static bool IsAgCompatible(string edition) => AgEditions.Contains(edition, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an annotation set (typically the resource's own) approves a backwards build move.</summary>
    public static bool IsDowngradeApproved(IDictionary<string, string>? annotations) =>
        annotations is not null
        && annotations.TryGetValue(AllowVersionDowngradeAnnotation, out var value)
        && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The image reference for a spec, and — when the tag is one the operator can read —
    /// the build it deploys.</summary>
    public static SqlImageResolution Resolve(
        string version, string edition, string? cumulativeUpdate = null, string? image = null, string? imageTag = null)
    {
        if (!IsValidCombination(version, edition))
            throw new ArgumentException($"Unsupported SQL Server version/edition combination: {version}/{edition}.");

        var reference = image ?? DefaultRepository;

        // A digest already pins the bytes; rewriting it with a tag would only weaken it.
        if (reference.Contains('@', StringComparison.Ordinal))
            return new SqlImageResolution(reference, null, null, IsFloating: false);

        var (repository, bakedTag) = SplitTag(reference);

        var tag = imageTag;
        if (tag is null && cumulativeUpdate is not null
            && SqlServerBuild.TryParsePatchLevel(cumulativeUpdate, out var cu, out var gdr))
            tag = DefaultBuildFor(version).WithPatchLevel(cu, gdr).Tag;
        tag ??= bakedTag ?? DefaultBuildFor(version).Tag;

        return new SqlImageResolution(
            $"{repository}:{tag}",
            tag,
            SqlServerBuild.TryParseTag(tag, out var build) ? build : null,
            SqlServerBuild.IsFloating(tag));
    }

    /// <summary>Resolve without throwing on an unsupported version/edition (the caller reports that
    /// as its own admission failure).</summary>
    public static bool TryResolve(
        string version, string edition, string? cumulativeUpdate, string? image, string? imageTag,
        out SqlImageResolution resolution)
    {
        if (!IsValidCombination(version, edition))
        {
            resolution = default;
            return false;
        }

        resolution = Resolve(version, edition, cumulativeUpdate, image, imageTag);
        return true;
    }

    public static string GetImage(
        string version, string edition, string? cumulativeUpdate = null, string? customImage = null, string? customTag = null) =>
        Resolve(version, edition, cumulativeUpdate, customImage, customTag).Image;

    /// <summary>
    /// Admission rules for how an instance selects its build. Returns the
    /// rejection message, or null; non-fatal remarks are appended to <paramref name="warnings"/>.
    /// </summary>
    public static string? CheckSelection(
        string version, string edition, string? cumulativeUpdate, string? image, string? imageTag,
        List<string>? warnings = null)
    {
        if (!IsValidCombination(version, edition))
            return $"Invalid version/edition combination: {version}/{edition}";

        // Two fields naming the build is one too many: the operator would have to pick a winner, and
        // the manifest would not say which. Make the author choose.
        if (cumulativeUpdate is not null && imageTag is not null)
            return "cumulativeUpdate and imageTag both pin the build — set one. Use cumulativeUpdate (e.g. \"CU25\") " +
                   "for the images the operator ships, and imageTag only for a tag it does not know how to build.";

        if (cumulativeUpdate is not null
            && !SqlServerBuild.TryParsePatchLevel(cumulativeUpdate, out _, out _))
            return $"cumulativeUpdate '{cumulativeUpdate}' is not a patch level: expected RTM, CU<n>, or CU<n>-GDR<n> (e.g. \"CU25\").";

        var resolution = Resolve(version, edition, cumulativeUpdate, image, imageTag);

        // A floating tag is re-resolved by the kubelet on every pull, so the engine can move
        // under the databases with nothing in the spec changing — and for an AG, land replicas on
        // different builds mid-rolling-update.
        if (resolution.IsFloating)
            return $"image '{resolution.Image}' is not pinned to a build: a floating tag lets the engine change under the " +
                   "databases without the spec changing, and can leave AG replicas on different builds. " +
                   "Pin it with cumulativeUpdate, an explicit imageTag, or a digest.";

        if (resolution.Build is { } build && build.Major.ToString(CultureInfo.InvariantCulture) != version)
            return $"the image tag deploys SQL Server {build.Major} while spec version is {version}. " +
                   "The engine version is what determines the database format: a mismatch either strands the databases " +
                   "on a build that cannot open them, or upgrades them irreversibly without anyone deciding to. " +
                   "Align the version with the image.";

        // A tag (or digest) the operator cannot read is allowed — it is the escape hatch — but the
        // guards that depend on reading it are then off, and saying so beats implying protection.
        if (resolution.Build is null)
            warnings?.Add($"image '{resolution.Image}' does not name a SQL Server build the operator can read, so the " +
                          "version and CU-downgrade guards cannot apply to it. Make sure it deploys SQL Server " +
                          $"{version} at a build no older than the one already running.");

        return null;
    }

    /// <summary>
    /// Whether an in-place build change is one SQL Server can actually perform. Returns the rejection
    /// message, or null when the move is forward (or unreadable, and so not ours to judge).
    /// </summary>
    public static string? CheckUpgrade(SqlImageResolution from, SqlImageResolution to)
    {
        if (from.Build is not { } fromBuild || to.Build is not { } toBuild)
            return null;

        if (toBuild.Major < fromBuild.Major)
            return $"Version downgrade from {fromBuild.Major} to {toBuild.Major} is not supported.";

        if (toBuild.Major == fromBuild.Major && toBuild < fromBuild)
            return $"Cumulative update downgrade from {fromBuild.PatchLevel} to {toBuild.PatchLevel} " +
                   $"(SQL Server {fromBuild.Major}) is not supported in place: the databases have already been upgraded " +
                   "by the newer build, and the older binaries cannot open them — an AG replica cannot rejoin an older " +
                   $"primary either. Restore onto an instance at the older build, or set the annotation " +
                   $"{AllowVersionDowngradeAnnotation}=true to take responsibility for the rollback.";

        return null;
    }

    /// <summary>Splits a reference into repository and tag. The colon of a registry port
    /// (<c>registry:5000/mssql/server</c>) is not a tag separator — only a colon after the last
    /// slash is.</summary>
    private static (string Repository, string? Tag) SplitTag(string reference)
    {
        var colon = reference.LastIndexOf(':');
        return colon > reference.LastIndexOf('/')
            ? (reference[..colon], reference[(colon + 1)..])
            : (reference, null);
    }

    private static bool TryMajor(string? version, out int major) =>
        int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out major);
}

/// <summary>What an instance will actually pull: the reference, its tag, and — when the operator can
/// read the tag — the <see cref="SqlServerBuild"/> it deploys.</summary>
/// <param name="Image">The full reference passed to the container runtime.</param>
/// <param name="Tag">The tag portion, or null for a digest-pinned reference.</param>
/// <param name="Build">The build the tag names, or null for a tag the operator cannot read.</param>
/// <param name="IsFloating">The tag does not name a fixed build (e.g. <c>2022-latest</c>).</param>
public readonly record struct SqlImageResolution(string Image, string? Tag, SqlServerBuild? Build, bool IsFloating)
{
    /// <summary>The patch level actually deployed (<c>CU25</c>), or null when the tag is not readable.</summary>
    public string? CumulativeUpdate => Build?.PatchLevel;
}
