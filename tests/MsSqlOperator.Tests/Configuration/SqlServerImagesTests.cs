using MsSqlOperator.Configuration;

namespace MsSqlOperator.Tests.Configuration;

public class SqlServerImagesTests
{
    private static SqlImageSelection Select(string version = "2022", string edition = "Developer",
        string? cumulativeUpdate = null, string? baseOs = null, string? image = null, string? imageTag = null) =>
        new(version, edition, cumulativeUpdate, baseOs, image, imageTag);

    [Theory]
    [InlineData("2022", "Developer")]
    [InlineData("2022", "Enterprise")]
    [InlineData("2022", "Standard")]
    [InlineData("2019", "Developer")]
    [InlineData("2019", "Enterprise")]
    [InlineData("2025", "Developer")]
    public void IsValidCombination_ValidVersionEdition_ReturnsTrue(string version, string edition)
    {
        Assert.True(SqlServerImages.IsValidCombination(version, edition));
    }

    [Theory]
    [InlineData("2099", "Developer")]
    [InlineData("2022", "InvalidEdition")]
    public void IsValidCombination_InvalidVersionEdition_ReturnsFalse(string version, string edition)
    {
        Assert.False(SqlServerImages.IsValidCombination(version, edition));
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Developer")]
    public void IsAgCompatible_EnterpriseDeveloper_ReturnsTrue(string edition)
    {
        Assert.True(SqlServerImages.IsAgCompatible(edition));
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Express")]
    [InlineData("Web")]
    public void IsAgCompatible_NonAgEdition_ReturnsFalse(string edition)
    {
        Assert.False(SqlServerImages.IsAgCompatible(edition));
    }

    [Theory]
    [InlineData("2019", "2022")]
    [InlineData("2019", "2025")]
    [InlineData("2022", "2025")]
    public void CheckUpgrade_MajorVersionUpgrade_IsAccepted(string from, string to)
    {
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select(from)),
            SqlServerImages.Resolve(Select(to))));
    }

    [Theory]
    [InlineData("2022", "2019")]
    [InlineData("2025", "2019")]
    public void CheckUpgrade_MajorVersionDowngrade_IsRejected(string from, string to)
    {
        var error = SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select(from)),
            SqlServerImages.Resolve(Select(to)));

        Assert.NotNull(error);
        Assert.Contains($"Version downgrade from {from} to {to}", error);
    }

    [Theory]
    [InlineData("2022", "Developer")]
    [InlineData("2019", "Enterprise")]
    public void GetImage_ValidCombination_ReturnsImage(string version, string edition)
    {
        var image = SqlServerImages.GetImage(Select(version, edition));
        Assert.False(string.IsNullOrEmpty(image));
        Assert.Contains("mcr.microsoft.com/mssql/server", image);
    }

    // ── Resolution ────────────────────────────────────────

    [Fact]
    public void Resolve_NoPin_UsesTheOperatorsPinnedDefault()
    {
        var resolution = SqlServerImages.Resolve(Select("2022"));

        Assert.Equal(SqlServerImages.DefaultBuildFor("2022"), resolution.Build);
        Assert.False(resolution.IsFloating);
    }

    [Fact]
    public void Resolve_CumulativeUpdate_KeepsTheDefaultOsBaseOfThatVersion()
    {
        var resolution = SqlServerImages.Resolve(Select("2022", cumulativeUpdate: "CU19"));

        Assert.Equal("mcr.microsoft.com/mssql/server:2022-CU19-ubuntu-22.04", resolution.Image);
        Assert.Equal("CU19", resolution.CumulativeUpdate);
        Assert.Equal("ubuntu-22.04", resolution.BaseOs);
    }

    [Fact]
    public void Resolve_BaseOs_KeepsTheDefaultCumulativeUpdate()
    {
        // Each knob moves only what it names: re-basing must not silently change the engine build.
        var resolution = SqlServerImages.Resolve(Select("2022", baseOs: "ubuntu-20.04"));

        Assert.Equal("ubuntu-20.04", resolution.BaseOs);
        Assert.Equal(SqlServerImages.DefaultBuildFor("2022").PatchLevel, resolution.CumulativeUpdate);
    }

    [Fact]
    public void Resolve_CumulativeUpdate_UsesTheSpellingTheRegistryPublished()
    {
        // SQL Server 2022 CU2 is published only off the RTM baseline — "2022-CU2-ubuntu-20.04" does
        // not exist, only "2022-RTM-CU2-ubuntu-20.04". So the tag is looked up, not rendered.
        var resolution = SqlServerImages.Resolve(Select("2022", cumulativeUpdate: "CU2", baseOs: "ubuntu-20.04"));

        Assert.Equal("mcr.microsoft.com/mssql/server:2022-RTM-CU2-ubuntu-20.04", resolution.Image);
        Assert.Equal(2, resolution.Build!.Value.CumulativeUpdate);
    }

    [Fact]
    public void Resolve_BuildPublishedUnderTwoSpellings_PrefersTheCanonicalOne()
    {
        // CU1 exists both as "2022-CU1-…" and "2022-RTM-CU1-…"; either pulls the same build, so the
        // choice only has to be deterministic — and the short one is the one people recognise.
        var resolution = SqlServerImages.Resolve(Select("2022", cumulativeUpdate: "CU1", baseOs: "ubuntu-20.04"));

        Assert.Equal("mcr.microsoft.com/mssql/server:2022-CU1-ubuntu-20.04", resolution.Image);
    }

    [Fact]
    public void Resolve_CumulativeUpdateAndBaseOs_CombineIntoOneTag()
    {
        var resolution = SqlServerImages.Resolve(Select("2025", cumulativeUpdate: "CU6", baseOs: "ubuntu-24.04"));

        Assert.Equal("mcr.microsoft.com/mssql/server:2025-CU6-ubuntu-24.04", resolution.Image);
    }

    [Fact]
    public void Resolve_CustomRepositoryWithTagAndImageTag_DoesNotConcatenateTwoTags()
    {
        // The old GetImage produced "repo:mirror-tag:2022-CU25-..." — an unpullable reference.
        var resolution = SqlServerImages.Resolve(Select("2022",
            image: "registry.internal:5000/mirror/mssql:2022-CU24-ubuntu-22.04",
            imageTag: "2022-CU25-ubuntu-22.04"));

        Assert.Equal("registry.internal:5000/mirror/mssql:2022-CU25-ubuntu-22.04", resolution.Image);
        Assert.Equal(25, resolution.Build!.Value.CumulativeUpdate);
    }

    [Fact]
    public void Resolve_DigestPinnedImage_IsLeftUntouched()
    {
        const string digest = "registry.internal/mssql@sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var resolution = SqlServerImages.Resolve(Select("2022", image: digest));

        Assert.Equal(digest, resolution.Image);
        Assert.Null(resolution.Build);
        Assert.False(resolution.IsFloating);   // a digest is the strongest pin there is
    }

    // ── The catalogue itself ──────────────────────────────

    [Theory]
    [InlineData("2019", "ubuntu-20.04")]
    [InlineData("2022", "ubuntu-22.04")]
    [InlineData("2025", "ubuntu-22.04")]
    public void PinnedImage_RunsOnTheDeclaredDefaultBaseOs(string version, string expectedBaseOs)
    {
        // The guard that makes baseOs meaningful: the default base is declared in BaseOsCatalog, but
        // the pinned tags are Renovate's to move. A bump that switched the OS base would re-base every
        // CU-pinned instance in the fleet — so it has to fail here first and be decided by a human.
        Assert.Equal(expectedBaseOs, SqlServerImages.SupportedBaseOsFor(version)[0]);
        Assert.Equal(expectedBaseOs, SqlServerImages.PinnedBaseOsFor(version));
    }

    [Theory]
    [InlineData("2019")]
    [InlineData("2022")]
    [InlineData("2025")]
    public void PinnedImage_IsATagTheRegistryActuallyPublishes(string version)
    {
        // Catches an edit to a tag the snapshot does not know: either the tag is wrong, or the
        // snapshot is stale and `dotnet run -- refresh-images` is owed.
        var warnings = new List<string>();

        Assert.Null(SqlServerImages.CheckSelection(Select(version), warnings));
        Assert.Empty(warnings);
    }

    /// <summary>How many CUs the shipped default may trail the newest published one before CI fails.
    /// Non-zero on purpose: a routine `refresh-images` that brings one new CU should not force a
    /// same-PR default bump (the snapshot is facts, the default is policy — separate decisions). But
    /// a default left behind for several releases — the SQL Server 2025 default sat on RTM while CU6
    /// shipped — is a silent staleness this makes loud.</summary>
    private const int MaxDefaultCuLag = 2;

    [Theory]
    [InlineData("2019")]
    [InlineData("2022")]
    [InlineData("2025")]
    public void PinnedImage_IsNotStale(string version)
    {
        var defaultCu = SqlServerImages.DefaultBuildFor(version).CumulativeUpdate;
        var latestCu = SqlServerImages.LatestPublishedCumulativeUpdate(version);

        Assert.True(defaultCu >= latestCu - MaxDefaultCuLag,
            $"SQL Server {version} default is CU{defaultCu} but the registry snapshot publishes up to CU{latestCu}. " +
            $"Bump the pinned default in SqlServerImages.cs (or, if intentional, raise MaxDefaultCuLag).");
    }

    [Theory]
    [InlineData("2019", "ubuntu-20.04")]
    [InlineData("2022", "ubuntu-22.04")]
    [InlineData("2022", "ubuntu-20.04")]
    [InlineData("2025", "ubuntu-22.04")]
    [InlineData("2025", "ubuntu-24.04")]
    public void EveryDeclaredBaseOs_HasAtLeastOnePublishedBuild(string version, string baseOs)
    {
        // BaseOsCatalog is hand-declared; this keeps it honest against the registry.
        Assert.Contains(baseOs, SqlServerImages.SupportedBaseOsFor(version));
        Assert.NotEmpty(SqlServerImages.PublishedCumulativeUpdatesFor(version, baseOs));
    }

    // ── Admission: how the build is selected ──────────────

    [Fact]
    public void CheckSelection_FloatingTag_IsRejected()
    {
        var error = SqlServerImages.CheckSelection(Select("2022", imageTag: "2022-latest"));

        Assert.NotNull(error);
        Assert.Contains("not pinned", error);
    }

    [Fact]
    public void CheckSelection_CumulativeUpdateAndImageTag_IsRejected()
    {
        var error = SqlServerImages.CheckSelection(
            Select("2022", cumulativeUpdate: "CU25", imageTag: "2022-CU24-ubuntu-22.04"));

        Assert.NotNull(error);
        Assert.Contains("set one", error);
    }

    [Fact]
    public void CheckSelection_BaseOsAndImageTag_IsRejected()
    {
        // The tag already carries an OS base; honouring both would mean picking a winner in silence.
        var error = SqlServerImages.CheckSelection(
            Select("2022", baseOs: "ubuntu-20.04", imageTag: "2022-CU25-ubuntu-22.04"));

        Assert.NotNull(error);
        Assert.Contains("set one", error);
    }

    [Fact]
    public void CheckSelection_CatalogKnobsAndATagBakedIntoImage_IsRejected()
    {
        var error = SqlServerImages.CheckSelection(
            Select("2022", cumulativeUpdate: "CU19", image: "registry.internal/mssql:2022-CU25-ubuntu-22.04"));

        Assert.NotNull(error);
        Assert.Contains("already carries the tag", error);
    }

    [Fact]
    public void CheckSelection_BaseOsNotPublishedForThatVersion_IsRejected()
    {
        // 2019 never shipped on 24.04: resolving it would produce a tag that does not exist, i.e. an
        // ImagePullBackOff at 3am instead of a rejection at apply time.
        var error = SqlServerImages.CheckSelection(Select("2019", baseOs: "ubuntu-24.04"));

        Assert.NotNull(error);
        Assert.Contains("not published for SQL Server 2019", error);
        Assert.Contains("ubuntu-20.04", error);
    }

    [Fact]
    public void CheckSelection_NonDefaultButPublishedBaseOs_IsAccepted()
    {
        var warnings = new List<string>();
        var error = SqlServerImages.CheckSelection(Select("2025", cumulativeUpdate: "CU6", baseOs: "ubuntu-24.04"), warnings);

        Assert.Null(error);
        Assert.Empty(warnings);
    }

    // ── Admission: was this build ever published? ─────────

    [Fact]
    public void CheckSelection_CuThatNeverShippedOnTheDefaultBase_IsRejected()
    {
        // The hole this closes: 2022's Ubuntu 22.04 images only start at CU10, and 22.04 is the
        // operator's DEFAULT base — so "cumulativeUpdate: CU5" used to resolve to a tag that has never
        // existed, pass admission without a murmur, and surface hours later as an ImagePullBackOff.
        var error = SqlServerImages.CheckSelection(Select("2022", cumulativeUpdate: "CU5"));

        Assert.NotNull(error);
        Assert.Contains("never published on ubuntu-22.04", error);
        Assert.Contains("ubuntu-20.04", error);
    }

    [Fact]
    public void CheckSelection_CuOnABaseItNeverShippedOn_IsRejected()
    {
        // 2025 RTM exists on 24.04 only as a "-preview" tag — "2025-RTM-ubuntu-24.04" itself does not
        // exist. Both knobs name a base the version does ship on, so the combination clears the
        // per-field gates and only the published-tag lookup catches it.
        var error = SqlServerImages.CheckSelection(Select("2025", cumulativeUpdate: "RTM", baseOs: "ubuntu-24.04"));

        Assert.NotNull(error);
        Assert.Contains("never published on ubuntu-24.04", error);
    }

    [Fact]
    public void CheckSelection_CuNewerThanTheSnapshot_IsAcceptedWithAWarning()
    {
        // Forward compatibility is the whole reason this is not a strict allowlist: a CU released
        // after this operator's snapshot must stay deployable without upgrading the operator.
        var warnings = new List<string>();
        var error = SqlServerImages.CheckSelection(Select("2022", cumulativeUpdate: "CU99"), warnings);

        Assert.Null(error);
        Assert.Contains(warnings, w => w.Contains("registry snapshot"));
    }

    // ── Admission: builds known not to start on a pinned cpuset ──

    [Theory]
    [InlineData("RTM")]
    [InlineData("CU6")]
    [InlineData("CU6-GDR1")]
    public void CheckKnownDefects_2025BeforeCu7_IsRejected(string cumulativeUpdate)
    {
        // Observed on a test cluster (NRI balloons): up to CU6-GDR1 the engine asserts in sqlpalstubs.cpp before
        // it listens, on any cpuset that does not start with the host's first CPUs.
        var error = SqlServerImages.CheckKnownDefects(Select("2025", cumulativeUpdate: cumulativeUpdate), previous: null);

        Assert.NotNull(error);
        Assert.Contains("CPUs are pinned", error);
        Assert.Contains("CU7", error);
    }

    [Theory]
    [InlineData("CU7")]
    [InlineData("CU10")]
    public void CheckKnownDefects_2025FromCu7_IsAccepted(string cumulativeUpdate)
    {
        Assert.Null(SqlServerImages.CheckKnownDefects(Select("2025", cumulativeUpdate: cumulativeUpdate), previous: null));
    }

    [Theory]
    [InlineData("2019")]
    [InlineData("2022")]
    [InlineData("2025")]
    public void CheckKnownDefects_TheDefaultBuild_IsNotDefective(string version)
    {
        Assert.Null(SqlServerImages.CheckKnownDefects(Select(version), previous: null));
    }

    [Fact]
    public void CheckKnownDefects_UnchangedDefectiveBuild_OnlyWarns()
    {
        // An instance already on CU6 (which runs on a node that does not pin CPUs) must stay editable.
        var selection = Select("2025", cumulativeUpdate: "CU6");
        var warnings = new List<string>();

        Assert.Null(SqlServerImages.CheckKnownDefects(selection, previous: selection, warnings));
        Assert.Contains(warnings, w => w.Contains("CPUs are pinned"));
    }

    [Fact]
    public void CheckKnownDefects_MovingOntoADefectiveBuild_IsRejected()
    {
        var error = SqlServerImages.CheckKnownDefects(Select("2025", cumulativeUpdate: "CU6"),
            previous: Select("2025", cumulativeUpdate: "CU5"));

        Assert.NotNull(error);
    }

    [Fact]
    public void CheckKnownDefects_UnreadableTag_IsNotJudged()
    {
        Assert.Null(SqlServerImages.CheckKnownDefects(
            Select("2025", image: "registry.internal/mssql@sha256:" + new string('a', 64)), previous: null));
    }

    [Fact]
    public void CheckSelection_PrivateMirror_IsNotCheckedAgainstTheSnapshot()
    {
        // A mirror carries whichever subset its owner chose; the snapshot says nothing about it.
        var error = SqlServerImages.CheckSelection(
            Select("2022", cumulativeUpdate: "CU5", image: "registry.internal/mssql"));

        Assert.Null(error);
    }

    [Fact]
    public void CheckSelection_ImageTagOfAnotherMajorVersion_IsRejected()
    {
        var error = SqlServerImages.CheckSelection(Select("2022", imageTag: "2019-CU32-ubuntu-20.04"));

        Assert.NotNull(error);
        Assert.Contains("deploys SQL Server 2019", error);
    }

    [Fact]
    public void CheckSelection_UnreadableCustomTag_IsAcceptedWithAWarning()
    {
        var warnings = new List<string>();
        var error = SqlServerImages.CheckSelection(
            Select("2022", image: "registry.internal/mssql", imageTag: "internal-build-42"), warnings);

        Assert.Null(error);
        Assert.Contains(warnings, w => w.Contains("guards cannot apply"));
    }

    [Fact]
    public void CheckSelection_PinnedCu_IsAccepted()
    {
        var warnings = new List<string>();

        Assert.Null(SqlServerImages.CheckSelection(Select("2022", cumulativeUpdate: "CU19"), warnings));
        Assert.Empty(warnings);
    }

    // ── Admission: which build moves are possible ─────────

    [Theory]
    [InlineData("CU25", "CU19")]     // CU rollback: the databases are already upgraded
    [InlineData("CU25", "RTM")]
    public void CheckUpgrade_CuDowngrade_IsRejected(string from, string to)
    {
        var error = SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: from)),
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: to)));

        Assert.NotNull(error);
        Assert.Contains("Cumulative update downgrade", error);
    }

    [Theory]
    [InlineData("CU19", "CU25")]
    [InlineData("RTM", "CU1")]
    [InlineData("CU25", "CU25")]
    public void CheckUpgrade_ForwardOrUnchanged_IsAccepted(string from, string to)
    {
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: from)),
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: to))));
    }

    [Fact]
    public void CheckUpgrade_ReBasingTheSameBuild_IsNotAVersionMove()
    {
        // Same engine build on a newer Ubuntu is a pod restart, not an upgrade — and certainly not a
        // downgrade, which is what an OS-aware comparison would have called 22.04 → 20.04.
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: "CU25", baseOs: "ubuntu-22.04")),
            SqlServerImages.Resolve(Select("2022", cumulativeUpdate: "CU25", baseOs: "ubuntu-20.04"))));
    }

    [Fact]
    public void CheckUpgrade_UnreadableTag_IsNotJudged()
    {
        // A custom image the operator cannot read is the escape hatch: it cannot be compared, so it
        // must not be guessed at either way.
        Assert.Null(SqlServerImages.CheckUpgrade(
            SqlServerImages.Resolve(Select("2022", image: "registry.internal/mssql", imageTag: "build-42")),
            SqlServerImages.Resolve(Select("2022", image: "registry.internal/mssql", imageTag: "build-41"))));
    }

    [Fact]
    public void IsDowngradeApproved_OnlyOnTheExplicitAnnotation()
    {
        Assert.True(SqlServerImages.IsDowngradeApproved(
            new Dictionary<string, string> { [SqlServerImages.AllowVersionDowngradeAnnotation] = "true" }));
        Assert.False(SqlServerImages.IsDowngradeApproved(
            new Dictionary<string, string> { [SqlServerImages.AllowVersionDowngradeAnnotation] = "false" }));
        Assert.False(SqlServerImages.IsDowngradeApproved(null));
    }
}
