using MsSqlOperator.Operator.Webhooks;
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

        // The job name reaches msdb procedures — restrict it upfront.
        if (Services.SqlServer.SqlIdentifierRules.Check(entity.Spec.Job.Name, "job.name") is { } jobNameErr)
            return Fail(jobNameErr, 422);

        if (entity.Spec.Job.Steps == null || entity.Spec.Job.Steps.Count == 0)
            return Fail("job must have at least one step", 422);

        var stepOrders = new HashSet<int>();
        foreach (var step in entity.Spec.Job.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                return Fail("Each step must have a name", 422);
            if (string.IsNullOrWhiteSpace(step.Command))
                return Fail($"Step '{step.Name}' must have a command", 422);
            if (step.Order < 1)
                return Fail($"Step '{step.Name}': order must be >= 1", 422);
            if (!stepOrders.Add(step.Order))
                return Fail($"Duplicate step order {step.Order} in step '{step.Name}'", 422);
            // An explicit empty string is invalid, not "use the default".
            if (step.Type != null && !ValidStepTypes.Contains(step.Type))
                return Fail($"Step '{step.Name}' has invalid type '{step.Type}'. Allowed: {string.Join(", ", ValidStepTypes)}", 422);
            if (!ValidStepActions.Contains(step.OnSuccess))
                return Fail($"Step '{step.Name}' has invalid onSuccess '{step.OnSuccess}'. Allowed: {string.Join(", ", ValidStepActions)}", 422);
            if (!ValidStepActions.Contains(step.OnFailure))
                return Fail($"Step '{step.Name}' has invalid onFailure '{step.OnFailure}'. Allowed: {string.Join(", ", ValidStepActions)}", 422);
            // GoToStep requires its target step id.
            if (step.OnSuccess.Equals("GoToStep", StringComparison.OrdinalIgnoreCase) && step.OnSuccessStep is null)
                return Fail($"Step '{step.Name}': onSuccess=GoToStep requires onSuccessStep", 422);
            if (step.OnFailure.Equals("GoToStep", StringComparison.OrdinalIgnoreCase) && step.OnFailureStep is null)
                return Fail($"Step '{step.Name}': onFailure=GoToStep requires onFailureStep", 422);
            if (step.RetryAttempts < 0 || step.RetryIntervalMinutes < 0)
                return Fail($"Step '{step.Name}': retryAttempts/retryIntervalMinutes must be >= 0", 422);
        }

        foreach (var schedule in entity.Spec.Job.Schedules ?? [])
        {
            if (schedule.FrequencyType != null && !ValidFrequencyTypes.Contains(schedule.FrequencyType))
                return Fail($"Schedule '{schedule.Name}' has invalid frequencyType '{schedule.FrequencyType}'. Allowed: {string.Join(", ", ValidFrequencyTypes)}", 422);

            // frequencyInterval bounds depend on frequencyType.
            var maxInterval = schedule.FrequencyType?.ToLowerInvariant() switch
            {
                "weekly" => 127, // bitmask Sun..Sat
                "monthly" => 31,
                _ => int.MaxValue,
            };
            if (schedule.FrequencyType?.ToLowerInvariant() is "weekly" or "monthly"
                && (schedule.FrequencyInterval < 1 || schedule.FrequencyInterval > maxInterval))
                return Fail($"Schedule '{schedule.Name}': frequencyInterval for {schedule.FrequencyType} must be 1-{maxInterval}", 422);

            if (schedule.StartTime != null && !System.Text.RegularExpressions.Regex.IsMatch(schedule.StartTime, "^[0-2][0-9][0-5][0-9][0-5][0-9]$"))
                return Fail($"Schedule '{schedule.Name}': startTime must be HHmmss", 422);
            if (schedule.StartDate != null && !DateTime.TryParseExact(schedule.StartDate, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return Fail($"Schedule '{schedule.Name}': startDate must be yyyyMMdd", 422);
        }

        return Success();
    }
}
