using System.Globalization;

namespace MsSqlOperator.Configuration;

/// <summary>
/// A pinned SQL Server build — the major version plus its patch level (RTM, a cumulative update,
/// and optionally a GDR security release) — parsed from an MCR image tag such as
/// <c>2022-CU25-ubuntu-22.04</c> and ordered the way the engine orders itself.
///
/// The build, not <c>spec.version</c>, decides what actually runs: the first start on a newer build
/// upgrades the databases, and the older binaries then refuse to open them. So a build is only ever
/// safe to move forward, which is exactly what <see cref="CompareTo"/> exists to let admission check.
///
/// Ordering caveat: GDR releases branch off a CU baseline, so <c>CU24-GDR1</c> sorts below
/// <c>CU25</c>. That matches the build numbers Microsoft ships and is what the downgrade guard needs;
/// it is not a claim that the two contain the same fixes.
/// </summary>
public readonly record struct SqlServerBuild(int Major, int CumulativeUpdate, int Gdr, string BaseOs)
    : IComparable<SqlServerBuild>
{
    /// <summary>RTM is CU 0 — the ordering the tags themselves imply.</summary>
    public bool IsRtm => CumulativeUpdate == 0;

    /// <summary>The patch component of a tag: <c>RTM</c>, <c>CU25</c>, <c>CU32-GDR1</c>.</summary>
    public string PatchLevel =>
        (IsRtm ? "RTM" : $"CU{CumulativeUpdate.ToString(CultureInfo.InvariantCulture)}")
        + (Gdr > 0 ? $"-GDR{Gdr.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    /// <summary>The image tag this build is published under, e.g. <c>2022-CU25-ubuntu-22.04</c>.</summary>
    public string Tag => string.IsNullOrEmpty(BaseOs)
        ? $"{Major.ToString(CultureInfo.InvariantCulture)}-{PatchLevel}"
        : $"{Major.ToString(CultureInfo.InvariantCulture)}-{PatchLevel}-{BaseOs}";

    /// <summary>The same major version and OS base at another patch level — how a requested
    /// <c>spec.cumulativeUpdate</c> becomes a concrete tag.</summary>
    public SqlServerBuild WithPatchLevel(int cumulativeUpdate, int gdr = 0) =>
        this with { CumulativeUpdate = cumulativeUpdate, Gdr = gdr };

    /// <summary>Major, then CU, then GDR. The OS base is not a version and never participates.</summary>
    public int CompareTo(SqlServerBuild other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (CumulativeUpdate != other.CumulativeUpdate) return CumulativeUpdate.CompareTo(other.CumulativeUpdate);
        return Gdr.CompareTo(other.Gdr);
    }

    public static bool operator <(SqlServerBuild left, SqlServerBuild right) => left.CompareTo(right) < 0;
    public static bool operator >(SqlServerBuild left, SqlServerBuild right) => left.CompareTo(right) > 0;
    public static bool operator <=(SqlServerBuild left, SqlServerBuild right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SqlServerBuild left, SqlServerBuild right) => left.CompareTo(right) >= 0;

    public override string ToString() => Tag;

    /// <summary>
    /// A tag that does not name a build — <c>2022-latest</c>, or no tag at all (Docker's implicit
    /// <c>:latest</c>). What it resolves to changes under the instance without anything in the spec
    /// changing, which is the one thing a database engine must never do.
    /// </summary>
    public static bool IsFloating(string? tag) =>
        string.IsNullOrWhiteSpace(tag) || tag.Contains("latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses an MCR tag (<c>2022-CU25-ubuntu-22.04</c>). False for floating or foreign tags.</summary>
    public static bool TryParseTag(string? tag, out SqlServerBuild build)
    {
        build = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var parts = tag.Split('-');
        if (parts.Length < 2) return false;

        if (parts[0].Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            return false;

        if (!TryParsePatch(parts, 1, out var cu, out var gdr, out var next)) return false;

        var baseOs = next < parts.Length ? string.Join('-', parts[next..]) : string.Empty;
        build = new SqlServerBuild(major, cu, gdr, baseOs);
        return true;
    }

    /// <summary>Parses <c>spec.cumulativeUpdate</c>: <c>RTM</c>, <c>CU25</c>, <c>CU32-GDR1</c>.</summary>
    public static bool TryParsePatchLevel(string? patchLevel, out int cumulativeUpdate, out int gdr)
    {
        cumulativeUpdate = 0;
        gdr = 0;
        if (string.IsNullOrWhiteSpace(patchLevel)) return false;

        var parts = patchLevel.Split('-');
        return TryParsePatch(parts, 0, out cumulativeUpdate, out gdr, out var next) && next == parts.Length;
    }

    /// <summary>Parses an MCR tag or throws — for the operator's own pinned catalogue, where an
    /// unparseable tag is a build-time mistake, not user input.</summary>
    public static SqlServerBuild ParseTag(string tag) =>
        TryParseTag(tag, out var build)
            ? build
            : throw new ArgumentException($"'{tag}' is not a pinned SQL Server image tag (expected e.g. 2022-CU25-ubuntu-22.04).", nameof(tag));

    /// <summary>RTM | CU&lt;n&gt; [ GDR&lt;n&gt; ], starting at <paramref name="index"/>.</summary>
    private static bool TryParsePatch(string[] parts, int index, out int cumulativeUpdate, out int gdr, out int next)
    {
        cumulativeUpdate = 0;
        gdr = 0;
        next = index;
        if (index >= parts.Length) return false;

        // RTM or a CU is the baseline. A tag in any other shape (a GDR with no CU baseline, a
        // vendor's own scheme, "latest") is not one this operator claims to understand: it falls
        // through to the custom-image path, where the caller is warned that the build guards are off
        // rather than being given a build that cannot be rendered back to its own tag.
        var token = parts[index];
        if (token.Equals("RTM", StringComparison.OrdinalIgnoreCase))
        {
            next = index + 1;
        }
        else if (TryParsePrefixed(token, "CU", out var cu))
        {
            cumulativeUpdate = cu;
            next = index + 1;
        }
        else
        {
            return false;
        }

        if (next < parts.Length && TryParsePrefixed(parts[next], "GDR", out var g))
        {
            gdr = g;
            next++;
        }

        return true;
    }

    /// <summary>"CU25" → 25, "GDR1" → 1, bare "GDR" → 1 (the first security release off that
    /// baseline). False when the prefix does not match or the remainder is not a number.</summary>
    private static bool TryParsePrefixed(string token, string prefix, out int value)
    {
        value = 0;
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var digits = token[prefix.Length..];
        if (digits.Length == 0)
        {
            if (prefix != "GDR") return false;
            value = 1;
            return true;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
