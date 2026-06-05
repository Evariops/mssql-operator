using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAgentJob))]
public class SqlAgentJobValidator : ValidationWebhook<SqlAgentJob>
{
    public override ValidationResult Create(SqlAgentJob entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlAgentJob oldEntity, SqlAgentJob entity, bool dryRun) => Validate(entity);

    private static readonly HashSet<string> ValidStepTypes = new(StringComparer.OrdinalIgnoreCase) { "TSQL", "CmdExec", "PowerShell", "SSIS", "SSAS" };
    private static readonly HashSet<string> ValidStepActions = new(StringComparer.OrdinalIgnoreCase) { "GoToNextStep", "QuitWithSuccess", "QuitWithFailure", "GoToStep" };
    private static readonly HashSet<string> ValidFrequencyTypes = new(StringComparer.OrdinalIgnoreCase) { "Once", "Daily", "Weekly", "Monthly" };

    private ValidationResult Validate(SqlAgentJob entity)
    {
        var targetError = TargetSelectorValidator.Validate(entity.Spec.Target);
        if (targetError != null) return Fail(targetError, 422);

        if (entity.Spec.Job == null)
            return Fail("job definition is required", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.Job.Name))
            return Fail("job.name is required", 422);

        if (entity.Spec.Job.Steps == null || entity.Spec.Job.Steps.Count == 0)
            return Fail("job must have at least one step", 422);

        var stepOrders = new HashSet<int>();
        foreach (var step in entity.Spec.Job.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                return Fail("Each step must have a name", 422);
            if (string.IsNullOrWhiteSpace(step.Command))
                return Fail($"Step '{step.Name}' must have a command", 422);
            if (!stepOrders.Add(step.Order))
                return Fail($"Duplicate step order {step.Order} in step '{step.Name}'", 422);
            if (!string.IsNullOrEmpty(step.Type) && !ValidStepTypes.Contains(step.Type))
                return Fail($"Step '{step.Name}' has invalid type '{step.Type}'. Allowed: {string.Join(", ", ValidStepTypes)}", 422);
            if (!string.IsNullOrEmpty(step.OnSuccess) && !ValidStepActions.Contains(step.OnSuccess))
                return Fail($"Step '{step.Name}' has invalid onSuccess '{step.OnSuccess}'. Allowed: {string.Join(", ", ValidStepActions)}", 422);
            if (!string.IsNullOrEmpty(step.OnFailure) && !ValidStepActions.Contains(step.OnFailure))
                return Fail($"Step '{step.Name}' has invalid onFailure '{step.OnFailure}'. Allowed: {string.Join(", ", ValidStepActions)}", 422);
        }

        if (entity.Spec.Job.Schedules != null)
        {
            foreach (var schedule in entity.Spec.Job.Schedules)
            {
                if (!string.IsNullOrEmpty(schedule.FrequencyType) && !ValidFrequencyTypes.Contains(schedule.FrequencyType))
                    return Fail($"Schedule '{schedule.Name}' has invalid frequencyType '{schedule.FrequencyType}'. Allowed: {string.Join(", ", ValidFrequencyTypes)}", 422);
            }
        }

        return Success();
    }
}
