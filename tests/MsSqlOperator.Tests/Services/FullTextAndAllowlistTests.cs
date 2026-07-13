using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// The full-text mitigations shipped without a single test —
/// neither the allowlist entries that make the crawl throttle expressible, nor the
/// fsGroupChangePolicy that keeps a multi-terabyte volume from being re-chowned on every mount.
/// </summary>
public class FullTextAndAllowlistTests
{
    [Theory]
    [InlineData("ft crawl bandwidth (max)")]
    [InlineData("ft crawl bandwidth (min)")]
    [InlineData("ft notify bandwidth (max)")]
    [InlineData("ft notify bandwidth (min)")]
    public void FullTextBandwidthOptions_AreAllowlisted(string option)
    {
        // These four are the only way to throttle a full-text crawl storm through a CR. They used to be
        // classified Unknown, so both the webhook and the reconciler rejected them: the mitigation for
        // a full-text crawl storm was literally inexpressible.
        Assert.Equal(SpConfigureAllowlist.Tier.Safe, SpConfigureAllowlist.Classify(option));
    }

    [Fact]
    public void FullTextBandwidthOfZero_ProducesAnAdmissionWarning()
    {
        // 0 means UNLIMITED. A user tightening the throttle to a "low" value of 0 would
        // in fact remove it.
        var validator = new SqlConfigurationValidator();
        var config = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                TargetMatchLabels = new Dictionary<string, string> { ["app"] = "cell" },
                SpConfigure = new SpConfigureSpec
                {
                    Advanced = [new AdvancedConfigParam { Name = "ft crawl bandwidth (max)", Value = 0 }],
                },
            },
        };

        var result = validator.Create(config, false);

        Assert.True(result.Valid);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("UNLIMITED"));
    }

    [Fact]
    public void EmptyTargetMatchLabels_IsRejected()
    {
        // An empty map matched EVERY instance in the namespace.
        var validator = new SqlConfigurationValidator();
        var config = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec { TargetMatchLabels = new Dictionary<string, string>() },
        };

        var result = validator.Create(config, false);

        Assert.False(result.Valid);
        Assert.Contains("EVERY SqlInstance", result.Message);
    }

    [Fact]
    public void ServerSettingsPathOutsideAManagedMount_IsRejected()
    {
        // Such a path relocated master.mdf and restarted the pod onto a blank master.
        var validator = new SqlConfigurationValidator();
        var config = new SqlConfiguration
        {
            Spec = new SqlConfigurationSpec
            {
                TargetMatchLabels = new Dictionary<string, string> { ["app"] = "cell" },
                ServerSettings = new ServerSettingsSpec { DefaultDataPath = "/custom/data" },
            },
        };

        var result = validator.Create(config, false);

        Assert.False(result.Valid);
        Assert.Contains("must be inside a volume the operator mounts", result.Message);
    }

    [Fact]
    public void UserPodSecurityContext_KeepsFsGroupChangePolicy()
    {
        // A user-provided podSecurityContext replaced the operator's default wholesale,
        // silently dropping OnRootMismatch — so Kubernetes re-chowned the whole data volume on every
        // single mount, adding minutes to each restart of a large instance.
        var merged = StatefulSetBuilder.MergePodSecurityContext(new k8s.Models.V1PodSecurityContext
        {
            RunAsUser = 2000, // the user cares about this one only
        });

        Assert.Equal(2000, merged.RunAsUser);
        Assert.Equal("OnRootMismatch", merged.FsGroupChangePolicy);
        Assert.Equal(10001, merged.FsGroup);
    }

    [Fact]
    public void NoPodSecurityContext_UsesTheSafeDefaults()
    {
        var merged = StatefulSetBuilder.MergePodSecurityContext(null);

        Assert.Equal("OnRootMismatch", merged.FsGroupChangePolicy);
        Assert.Equal(10001, merged.RunAsUser);
    }

    [Theory]
    [InlineData("2022", null, "2019-CU32-ubuntu-20.04")]   // tag deploys 2019 while spec says 2022
    [InlineData("2019", null, "2022-CU25-ubuntu-22.04")]   // and the reverse
    public void ImageTagDeployingAnotherMajorVersion_IsDetected(string declared, string? image, string tag)
    {
        // Only spec.version was validated, but image/imageTag decide what actually runs.
        var error = SqlServerImages.CheckImageMatchesVersion(declared, image, tag);
        Assert.NotNull(error);
        Assert.Contains("deploys SQL Server", error);
    }

    [Fact]
    public void ImageTagMatchingTheDeclaredVersion_IsAccepted()
    {
        Assert.Null(SqlServerImages.CheckImageMatchesVersion("2022", null, "2022-CU25-ubuntu-22.04"));
        Assert.Null(SqlServerImages.CheckImageMatchesVersion("2022", null, null));
    }

    [Fact]
    public void MaxServerMemory_LeavesHeadroomForFdhostAndCrawlBuffers()
    {
        // Fdhost and the full-text crawl buffers live OUTSIDE max server memory, so a
        // max server memory equal to the process limit leaves zero head-room inside the cgroup — and
        // the kernel OOM-kills the container during the population storm at startup.
        var spec = new MemorySpec { Total = "16Gi", SystemReserve = "2Gi" };

        var processLimit = MemoryCalculator.CalculateProcessMemoryLimitMb(spec);
        var maxServerMemory = MemoryCalculator.CalculateSqlMaxMemoryMb(spec);

        Assert.Equal(14336, processLimit);
        Assert.True(maxServerMemory < processLimit);
        Assert.True(processLimit - maxServerMemory >= 1024, "at least 1 GiB of intra-process head-room");
    }
}
