using FluentAssertions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlAgentJobValidatorTests
{
    private readonly SqlAgentJobValidator _validator = new();

    private static SqlAgentJob MakeJob(Action<SqlAgentJobSpec>? configure = null)
    {
        var spec = new SqlAgentJobSpec
        {
            Target = new TargetSelector { Instances = ["my-instance"] },
            Job = new JobDefinitionSpec
            {
                Name = "nightly",
                Steps = [new JobStepSpec { Name = "step1", Order = 1, Command = "SELECT 1" }]
            }
        };
        configure?.Invoke(spec);
        return new SqlAgentJob { Spec = spec };
    }

    [Fact]
    public void Create_ValidJob_ReturnsSuccess()
    {
        _validator.Create(MakeJob(), false).Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_DuplicateStepOrder_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
        [
            new JobStepSpec { Name = "a", Order = 1, Command = "SELECT 1" },
            new JobStepSpec { Name = "b", Order = 1, Command = "SELECT 2" }
        ]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidStepType_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
            [new JobStepSpec { Name = "a", Order = 1, Command = "x", Type = "Bogus" }]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidOnFailureAction_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
            [new JobStepSpec { Name = "a", Order = 1, Command = "x", OnFailure = "Explode" }]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidFrequencyType_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Schedules =
            [new JobScheduleSpec { Name = "sched", FrequencyType = "Hourly" }]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_ValidEnumsAndSchedule_ReturnsSuccess()
    {
        var result = _validator.Create(MakeJob(s =>
        {
            s.Job!.Steps =
            [
                new JobStepSpec { Name = "a", Order = 1, Command = "x", Type = "TSQL", OnSuccess = "GoToNextStep" },
                new JobStepSpec { Name = "b", Order = 2, Command = "y", OnFailure = "QuitWithFailure" }
            ];
            s.Job.Schedules = [new JobScheduleSpec { Name = "sched", FrequencyType = "Weekly" }];
        }), false);
        result.Valid.Should().BeTrue();
    }
}
