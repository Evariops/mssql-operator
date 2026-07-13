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
                // A job whose owner is sa (the default) runs its steps as sysadmin and can
                // enable and call xp_cmdshell, bypassing the sp_configure allowlist — it now requires an
                // explicit opt-in. The fixture takes it so the other rules stay under test.
                Owner = "job_runner",
                Steps = [new JobStepSpec { Name = "step1", Order = 1, Command = "SELECT 1" }]
            }
        };
        configure?.Invoke(spec);
        return new SqlAgentJob { Spec = spec };
    }

    [Fact]
    public void Create_ValidJob_ReturnsSuccess()
    {
        Assert.True(_validator.Create(MakeJob(), false).Valid);
    }

    [Fact]
    public void Create_OwnerSa_WithoutAllowDangerous_ReturnsFail()
    {
        // Sysadmin-owned steps can enable and call xp_cmdshell — the very thing the
        // sp_configure allowlist gates. Require the same explicit opt-in.
        var result = _validator.Create(MakeJob(s => s.Job!.Owner = "sa"), false);
        Assert.False(result.Valid);
        Assert.Contains("allowDangerous", result.Message);
    }

    [Fact]
    public void Create_OwnerUnset_WithoutAllowDangerous_ReturnsFail()
    {
        // An unset owner defaults to sa in msdb — same risk, same gate.
        var result = _validator.Create(MakeJob(s => s.Job!.Owner = null), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_OwnerSa_WithAllowDangerous_ReturnsSuccess()
    {
        Assert.True(_validator.Create(MakeJob(s =>
        {
            s.Job!.Owner = "sa";
            s.Job.AllowDangerous = true;
        }), false).Valid);
    }

    [Fact]
    public void Create_CmdExecStep_WithoutAllowDangerous_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
            [new JobStepSpec { Name = "shell", Order = 1, Command = "rm -rf /", Type = "CmdExec" }]), false);
        Assert.False(result.Valid);
        Assert.Contains("allowDangerous", result.Message);
    }

    [Fact]
    public void Create_DuplicateStepOrder_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
        [
            new JobStepSpec { Name = "a", Order = 1, Command = "SELECT 1" },
            new JobStepSpec { Name = "b", Order = 1, Command = "SELECT 2" }
        ]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidStepType_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
            [new JobStepSpec { Name = "a", Order = 1, Command = "x", Type = "Bogus" }]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidOnFailureAction_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Steps =
            [new JobStepSpec { Name = "a", Order = 1, Command = "x", OnFailure = "Explode" }]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidFrequencyType_ReturnsFail()
    {
        var result = _validator.Create(MakeJob(s => s.Job!.Schedules =
            [new JobScheduleSpec { Name = "sched", FrequencyType = "Hourly" }]), false);
        Assert.False(result.Valid);
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
        Assert.True(result.Valid);
    }
}
