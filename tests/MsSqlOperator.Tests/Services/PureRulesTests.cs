using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

/// <summary>Small pure rules: database filters, TDE source consent, Agent job schedules and steps, image registries, CU bumps, the rollout budget, probes.</summary>
public class PureRulesTests
{
    //
    [Theory]
    [InlineData("app_.*|test_.*", "legacy_test_prod", false)]
    [InlineData("app_.*|test_.*", "test_x", true)]
    [InlineData("^app_.*$", "app_x", true)]
    public void DatabaseFilter_AnchorsEveryAlternative(string pattern, string database, bool match)
        => Assert.Equal(match, DatabaseFilter.BuildPatternRegex(pattern).IsMatch(database));

    [Fact]
    public void DatabaseFilter_MatchLabels_IsRefused()
    {
        var filter = new MsSqlOperator.Entities.Common.DatabaseFilterSpec { MatchLabels = new() { ["a"] = "b" } };
        Assert.Throws<InvalidOperationException>(() => new DatabaseFilter().Apply(["db"], null, filter));
        Assert.NotNull(MsSqlOperator.Validators.DatabaseFilterValidator.Validate(filter, "f"));
    }

    //
    [Theory]
    [InlineData("tenant-a, tenant-b", "tenant-b", true)]
    [InlineData("tenant-a", "tenant-b", false)]
    [InlineData(null, "tenant-b", false)]
    public void TdeSource_FromAnotherNamespace_NeedsItsOwnersConsent(string? allowed, string consumer, bool expected)
    {
        var secret = new V1Secret { Metadata = new V1ObjectMeta { Annotations = allowed is null ? null : new Dictionary<string, string> { [SqlTdeCertificateController.ConsumerNamespacesAnnotation] = allowed } } };
        Assert.Equal(expected, SqlTdeCertificateController.AllowsConsumer(secret, consumer));
    }

    //
    [Theory]
    [InlineData("083000", 83000)]
    [InlineData("08:30:00", 83000)]
    [InlineData(null, 0)]
    public void JobStartTime_IsHhmmss(string? value, int expected)
        => Assert.Equal(expected, SqlAgentJobController.ParseStartTime(value));

    //
    [Fact]
    public void WeeklySchedule_CarriesRecurrenceAndSubday()
    {
        var schedule = SqlAgentJobController.BuildSchedule(new JobScheduleSpec
        {
            Name = "s",
            FrequencyType = "Weekly",
            FrequencyInterval = 2,
            RecurrenceFactor = 2,
            SubdayType = "Minutes",
            SubdayInterval = 15,
            StartTime = "063000",
            StartDate = "20261005",
        });
        Assert.Equal((8, 2, 2, 4, 15, 63000, 20261005),
            (schedule.FreqType, schedule.FreqInterval, schedule.RecurrenceFactor, schedule.SubdayType, schedule.SubdayInterval,
             schedule.ActiveStartTime, schedule.ActiveStartDate));
    }

    //
    [Fact]
    public void AgJobSteps_StartWithThePrimaryGuard_AndMapGoToStepTargets()
    {
        var job = new JobDefinitionSpec
        {
            Name = "j",
            Steps =
            [
                new JobStepSpec { Name = "a", Order = 10, Command = "SELECT 1", OnFailure = "GoToStep", OnFailureStep = 20 },
                new JobStepSpec { Name = "b", Order = 20, Command = "SELECT 2" },
            ],
        };

        var steps = SqlAgentJobController.BuildSteps(job, "my-ag");

        Assert.Equal([1, 2, 3], steps.Select(s => s.StepId));
        Assert.Equal("QuitWithSuccess", steps[0].OnFailure);
        Assert.Contains("N'my-ag'", steps[0].Command, StringComparison.Ordinal);
        Assert.Equal(3, steps[1].OnFailStepId);
    }

    // The default GoToNextStep on the last step made every run fail ("tried to run a
    // non-existent step"). The last step quits; the earlier ones keep chaining.
    [Fact]
    public void LastJobStep_Quits_InsteadOfFallingThrough()
    {
        var job = new JobDefinitionSpec
        {
            Name = "j",
            Steps =
            [
                new JobStepSpec { Name = "b", Order = 20, Command = "SELECT 2", OnFailure = "GoToNextStep" },
                new JobStepSpec { Name = "a", Order = 10, Command = "SELECT 1" },
            ],
        };

        var steps = SqlAgentJobController.BuildSteps(job, "my-ag");

        Assert.Equal(("GoToNextStep", "QuitWithSuccess"), (steps[0].OnSuccess, steps[0].OnFailure));   // guard
        Assert.Equal(("GoToNextStep", "QuitWithFailure"), (steps[1].OnSuccess, steps[1].OnFailure));   // a
        Assert.Equal(("QuitWithSuccess", "QuitWithFailure"), (steps[2].OnSuccess, steps[2].OnFailure)); // b, last
        Assert.True(SqlAgentJobController.LastStepFallsThrough(job));

        // The marker changes only for such jobs: they are rewritten once, the others stay as they are.
        var quitting = new JobDefinitionSpec { Name = "k", Steps = [new JobStepSpec { Name = "x", Order = 1, Command = "SELECT 1", OnSuccess = "QuitWithSuccess" }] };
        Assert.False(SqlAgentJobController.LastStepFallsThrough(quitting));
    }

    //
    [Theory]
    [InlineData(null, true)]
    [InlineData("mcr.microsoft.com/mssql/server", true)]
    [InlineData("mcr.microsoft.com.evil.io/mssql/server", false)]
    [InlineData("docker.io/attacker/sql", false)]
    public void ImageRegistry_IsMatchedAsAPath(string? image, bool allowed)
        => Assert.Equal(allowed, ImagePolicy.IsAllowed(image, ["mcr.microsoft.com/"]));

    //
    [Fact]
    public void DefaultCuBump_IsHeldUntilApproved()
    {
        var selection = new SqlImageSelection("2022", "Developer");
        var desired = SqlServerImages.Resolve(selection);
        var cu = desired.CumulativeUpdate!;
        var older = desired.Image.Replace(cu, "CU1", StringComparison.Ordinal);

        var (held, why) = SqlServerImages.DecideEngineImage(selection, older, null, automatic: false);
        Assert.Equal(older, held);
        Assert.NotNull(why);

        var approved = new Dictionary<string, string> { [SqlServerImages.ApproveEngineUpgradeAnnotation] = cu };
        Assert.Equal(desired.Image, SqlServerImages.DecideEngineImage(selection, older, approved, automatic: false).Image);
        Assert.Equal(desired.Image, SqlServerImages.DecideEngineImage(selection with { CumulativeUpdate = cu }, older, null, automatic: false).Image);
        Assert.Equal(desired.Image, SqlServerImages.DecideEngineImage(selection, older, null, automatic: true).Image);
    }

    [Fact]
    public void RolloutBudget_CountsRollingAndUnreadyStatefulSets()
    {
        Assert.True(RolloutBudget.IsBusy(new V1StatefulSet { Spec = new V1StatefulSetSpec { Replicas = 1 }, Status = new V1StatefulSetStatus { ReadyReplicas = 0 } }));
        Assert.True(RolloutBudget.IsBusy(new V1StatefulSet { Spec = new V1StatefulSetSpec { Replicas = 1 }, Status = new V1StatefulSetStatus { ReadyReplicas = 1, CurrentRevision = "a", UpdateRevision = "b" } }));
        Assert.False(RolloutBudget.IsBusy(new V1StatefulSet { Spec = new V1StatefulSetSpec { Replicas = 1 }, Status = new V1StatefulSetStatus { ReadyReplicas = 1, CurrentRevision = "a", UpdateRevision = "a" } }));
    }

    //
    [Fact]
    public void Probes_NeverPutThePasswordInArgv()
    {
        var shell = StatefulSetBuilder.SqlcmdShell("-l 5 -b", "'SELECT 1'");
        Assert.DoesNotContain(" -P ", shell, StringComparison.Ordinal);
        Assert.Contains("SQLCMDPASSWORD=", shell, StringComparison.Ordinal);
        Assert.Contains(StatefulSetBuilder.SaPasswordMountPath, shell, StringComparison.Ordinal);
    }

    //
    [Fact]
    public void ConfigurationLedger_ImportsLegacyAnnotations_AndRoundTripsThroughTheStatus()
    {
        var config = new SqlConfiguration
        {
            Metadata = new V1ObjectMeta { Annotations = new Dictionary<string, string> { ["mssql-operator.evariops.com/managed-traceflags-sql-0"] = "1222,3226" } },
            Status = new SqlConfigurationStatus(),
        };

        var ledger = MsSqlOperator.Services.SqlServer.ConfigurationLedger.Load(config);
        ledger.RecordOriginal("sql-0", "max degree of parallelism", 0);
        ledger.RecordOriginal("sql-0", "max degree of parallelism", 4); // the first value seen wins
        ledger.Save(config.Status);

        Assert.True(ledger.ImportedLegacyAnnotations);
        Assert.Equal([1222, 3226], Assert.Single(config.Status.ManagedTraceFlags!).Flags);
        Assert.Equal(0, Assert.Single(config.Status.OriginalSettings!).Value);
    }
}
