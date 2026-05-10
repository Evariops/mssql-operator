using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAgentJob))]
public class SqlAgentJobValidator : ValidationWebhook<SqlAgentJob>
{
    public override ValidationResult Create(SqlAgentJob entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlAgentJob oldEntity, SqlAgentJob entity, bool dryRun) => Validate(entity);

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

        foreach (var step in entity.Spec.Job.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                return Fail("Each step must have a name", 422);
            if (string.IsNullOrWhiteSpace(step.Command))
                return Fail($"Step '{step.Name}' must have a command", 422);
        }

        return Success();
    }
}
