using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlAvailabilityGroupValidatorTests
{
    private readonly SqlAvailabilityGroupValidator _validator = new();

    private static SqlAvailabilityGroupSpec CreateValidSpec(string edition = "Enterprise", int count = 3, int syncCount = 2) => new()
    {
        Edition = edition,
        Replicas = new AgReplicaTopologySpec { Count = count, SynchronousCount = syncCount },
        SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
        MasterKeyPasswordSecretRef = new SecretReference { Name = "mk-secret", Key = "password" },
        HadrCertificate = new AgHadrCertificateSpec { Source = "Generate" },
        InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022" }
    };

    [Fact]
    public void Create_ValidEnterprise_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec() };
        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_ValidStandard_ReturnsSuccess()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Standard", 2, 2) };
        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Theory]
    [InlineData("cpu.preserve.resource-policy.nri.io", true)]
    [InlineData("vault.hashicorp.com/agent-inject", true)]
    [InlineData("plain-key_1.x", true)]
    [InlineData("mssql-operator.evariops.com/config-hash", false)]   // would pose as the operator's
    [InlineData("rollout.mssql-operator.evariops.com/x", false)]
    [InlineData("Bad_Prefix/key", false)]
    [InlineData("example.com/-key", false)]
    [InlineData("example.com/", false)]
    public void Create_PodAnnotations_KeysOutsideTheOperatorDomainOnly(string key, bool valid)
    {
        // Annotations reach the pod template as they are.
        var spec = CreateValidSpec();
        spec.InstanceTemplate!.PodAnnotations = new Dictionary<string, string> { [key] = "true" };
        Assert.Equal(valid, _validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_HadrCertificateImportSource_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.HadrCertificate!.Source = "Import";
        Assert.False(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_HadrCertificateUnsupportedModel_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.HadrCertificate!.Model = "Shared";
        Assert.False(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Theory]
    [InlineData("Sometimes", 80, 50, null)]
    [InlineData("Auto", 40, 50, null)]   // would grow volumes that are barely used
    [InlineData("Auto", 80, 5, null)]    // steps too small to outrun a burst
    [InlineData("Auto", 80, 50, "2Gi")]  // below the declared size (5Gi by default)
    [InlineData("Auto", 80, 50, "lots")]
    public void Create_InvalidVolumeAutoGrow_ReturnsFail(string mode, int threshold, int grow, string? logMaxSize)
    {
        var spec = CreateValidSpec();
        spec.VolumeAutoGrow = new AgVolumeAutoGrowSpec { Mode = mode, ThresholdPercent = threshold, GrowPercent = grow, LogMaxSize = logMaxSize };

        Assert.False(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_VolumeAutoGrowWithCeilings_ReturnsSuccess()
    {
        var spec = CreateValidSpec();
        spec.VolumeAutoGrow = new AgVolumeAutoGrowSpec { DataMaxSize = "100Gi", LogMaxSize = "40Gi" };

        Assert.True(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_StandardWithThreeReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Standard", 3, 2) };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_StandardWithARequiredSyncFloor_ReturnsFail()
    {
        // A basic availability group has no REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT.
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.Replicas.RequiredSyncFloor = 1;

        var result = _validator.Create(new SqlAvailabilityGroup { Spec = spec }, false);

        Assert.False(result.Valid);
        Assert.Contains("requiredSyncFloor is not available on Standard", result.Message);
    }

    [Fact]
    public void Create_StandardWithTheDefaultRequiredSync_ReturnsSuccess()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        Assert.Equal(1, spec.Replicas.RequiredSynchronizedSecondariesToCommit); // ignored there

        Assert.True(_validator.Create(new SqlAvailabilityGroup { Spec = spec }, false).Valid);
    }

    [Fact]
    public void Create_StandardWithReadOnlyListener_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.ReadOnlyListener = new AgReadOnlyListenerSpec { Port = 1434 };
        var entity = new SqlAvailabilityGroup { Spec = spec };
        var result = _validator.Create(entity, false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_StandardWithReadOnlyRouting_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.ReadOnlyRouting = new AgReadOnlyRoutingSpec { Enabled = true };
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_StandardWithBackupPreference_ReturnsFail()
    {
        var spec = CreateValidSpec("Standard", 2, 2);
        spec.BackupPreference = "PRIMARY";
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_EnterpriseTooManyReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 10, 2) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_TooFewReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 1, 1) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_SyncCountExceedsReplicas_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Enterprise", 3, 5) };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_InvalidEdition_ReturnsFail()
    {
        var entity = new SqlAvailabilityGroup { Spec = CreateValidSpec("Express") };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    // Database validation moved to SqlAvailabilityGroupDatabaseValidatorTests

    [Fact]
    public void Create_RequiredSyncSecondariesEqualsSyncCount_ReturnsFail()
    {
        var spec = CreateValidSpec();
        spec.Replicas.RequiredSynchronizedSecondariesToCommit = 2; // equals SynchronousCount
        var entity = new SqlAvailabilityGroup { Spec = spec };
        Assert.False(_validator.Create(entity, false).Valid);
    }

    // ── CU control (one build for the whole group) ────────

    private static SqlAvailabilityGroup Ag(string? cu = null, string? imageTag = null, string version = "2022",
        string? baseOs = null)
    {
        var spec = CreateValidSpec();
        spec.InstanceTemplate = new AgInstanceTemplateSpec
        {
            Version = version,
            CumulativeUpdate = cu,
            BaseOs = baseOs,
            ImageTag = imageTag,
        };
        return new SqlAvailabilityGroup { Spec = spec };
    }

    [Fact]
    public void Create_PinnedCumulativeUpdate_ReturnsSuccess()
    {
        Assert.True(_validator.Create(Ag(cu: "CU19"), false).Valid);
    }

    [Fact]
    public void Create_FloatingImageTag_ReturnsFail()
    {
        // A floating tag can land replicas of the same AG on different builds — the one state an AG
        // cannot sustain.
        Assert.False(_validator.Create(Ag(imageTag: "2022-latest"), false).Valid);
    }

    [Fact]
    public void Update_CumulativeUpdateUpgrade_ReturnsSuccess()
    {
        Assert.True(_validator.Update(Ag(cu: "CU19"), Ag(cu: "CU25"), false).Valid);
    }

    [Fact]
    public void Update_CumulativeUpdateDowngrade_ReturnsFail()
    {
        var result = _validator.Update(Ag(cu: "CU25"), Ag(cu: "CU19"), false);

        Assert.False(result.Valid);
        Assert.Contains("Cumulative update downgrade", result.Message);
    }

    [Fact]
    public void Create_2025BeforeCu7_ReturnsFail()
    {
        var result = _validator.Create(Ag(version: "2025", cu: "CU6"), false);

        Assert.False(result.Valid);
        Assert.Contains("CPUs are pinned", result.Message);
    }

    [Fact]
    public void Update_2025DefectiveBuildUnchanged_ReturnsSuccess()
    {
        Assert.True(_validator.Update(Ag(version: "2025", cu: "CU6"), Ag(version: "2025", cu: "CU6"), false).Valid);
    }

    [Fact]
    public void Create_BaseOsNotPublishedForThatVersion_ReturnsFail()
    {
        var result = _validator.Create(Ag(version: "2019", baseOs: "ubuntu-24.04"), false);

        Assert.False(result.Valid);
        Assert.Contains("not published for SQL Server 2019", result.Message);
    }

    [Fact]
    public void Create_PublishedBaseOs_ReturnsSuccess()
    {
        Assert.True(_validator.Create(Ag(cu: "CU25", baseOs: "ubuntu-20.04"), false).Valid);
    }

    // ── Durability ────────────────────────────

    [Theory]
    [InlineData("Enterprise", 3, 0, false, false)] // read-scale: no secondary to harden a commit
    [InlineData("Enterprise", 3, 1, false, false)]
    [InlineData("Enterprise", 3, 2, true, true)]   // a single synchronous secondary: writes block while it is down
    [InlineData("Enterprise", 2, 2, true, true)]
    [InlineData("Standard", 2, 2, false, false)]  // a basic AG does not support REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT
    [InlineData("Enterprise", 3, 3, true, false)]
    public void Create_ExplicitQuorum_NeedsASynchronousSecondary_AndWarnsWhenThereIsOnlyOne(
        string edition, int count, int syncCount, bool valid, bool warned)
    {
        var spec = CreateValidSpec(edition, count, syncCount);
        spec.Replicas.RequiredSynchronizedSecondariesToCommit = Math.Min(1, Math.Max(0, syncCount - 1));
        spec.Replicas.Durability = "Quorum";

        var result = _validator.Create(new SqlAvailabilityGroup { Spec = spec }, false);

        Assert.Equal(valid, result.Valid);
        Assert.Equal(warned, result.Warnings?.Any(w => w.Contains("Quorum with a single synchronous secondary", StringComparison.Ordinal)) == true);
    }

    [Theory]
    [InlineData("Enterprise", 3, "synchronousCount: 3 keeps writing")]
    [InlineData("Enterprise", 2, "Only a third synchronous replica")] // the replica count is immutable
    public void Create_ExplicitQuorumWithOneSynchronousSecondary_OnlySuggestsWhatTheTopologyAllows(string edition, int count, string advice)
    {
        var spec = CreateValidSpec(edition, count, 2);
        spec.Replicas.Durability = "Quorum";

        var result = _validator.Create(new SqlAvailabilityGroup { Spec = spec }, false);

        Assert.Contains(result.Warnings ?? [], w => w.Contains("Quorum with a single synchronous secondary", StringComparison.Ordinal)
                                                    && w.Contains(advice, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Standard", "Standard offers no other profile")]
    [InlineData("Enterprise", "durability: Quorum to lose none")]
    public void Create_TwoReplicasWithoutDurability_OffersQuorumOnlyWhereTheEngineSupportsIt(string edition, string advice)
    {
        var result = _validator.Create(new SqlAvailabilityGroup { Spec = CreateValidSpec(edition, 2, 2) }, false);

        Assert.Contains(result.Warnings ?? [], w => w.Contains("not set on a 2-replica AG", StringComparison.Ordinal)
                                                    && w.Contains(advice, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Legacy", 2, true)]  // switched to Quorum
    [InlineData("Quorum", 3, true)]  // down to one synchronous secondary
    [InlineData("Quorum", 2, false)] // already said
    public void Update_ExplicitQuorumWithOneSynchronousSecondary_WarnsWhenTheAgComesToIt(string oldDurability, int oldSync, bool warned)
    {
        var old = CreateValidSpec(syncCount: oldSync);
        old.Replicas.Durability = oldDurability;
        var spec = CreateValidSpec(syncCount: 2);
        spec.Replicas.Durability = "Quorum";

        var result = _validator.Update(new SqlAvailabilityGroup { Spec = old }, new SqlAvailabilityGroup { Spec = spec }, false);

        Assert.True(result.Valid);
        Assert.Equal(warned, result.Warnings?.Any(w => w.Contains("Quorum with a single synchronous secondary", StringComparison.Ordinal)) == true);
    }

    [Theory]
    [InlineData(2, null, true)]       // the default synchronousCount: runs Legacy, and is told so
    [InlineData(3, null, false)]      // runs Quorum
    [InlineData(2, "Legacy", false)]  // chosen
    public void Create_UnsetDurabilityBelowThreeSynchronous_WarnsItRunsLegacy(int syncCount, string? durability, bool warned)
    {
        var spec = CreateValidSpec(syncCount: syncCount);
        spec.Replicas.Durability = durability;

        var result = _validator.Create(new SqlAvailabilityGroup { Spec = spec }, false);

        Assert.True(result.Valid);
        Assert.Equal(warned, result.Warnings?.Any(w => w.Contains("synchronousCount is below 3", StringComparison.Ordinal)) == true);
    }

    [Theory]
    [InlineData(2, 2, false)]  // the profile was fixed at creation and is not repeated on every apply
    [InlineData(3, 3, false)]
    [InlineData(3, 2, true)]   // may leave a default Quorum with 1 synchronous secondary
    public void Update_UnsetDurability_WarnsOnlyWhenSynchronousCountGoesBelowThree(int oldSync, int newSync, bool warned)
    {
        var result = _validator.Update(new SqlAvailabilityGroup { Spec = CreateValidSpec(syncCount: oldSync) },
            new SqlAvailabilityGroup { Spec = CreateValidSpec(syncCount: newSync) }, false);

        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Warnings ?? [], w => w.Contains("synchronousCount is below 3", StringComparison.Ordinal));
        Assert.Equal(warned, result.Warnings?.Any(w => w.Contains("synchronousCount goes below 3", StringComparison.Ordinal)) == true);
    }
}
