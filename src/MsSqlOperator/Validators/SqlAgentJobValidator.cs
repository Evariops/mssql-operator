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

        // A job owned by sa runs its steps as sysadmin, so a plain T-SQL step can enable
        // and call xp_cmdshell — bypassing the sp_configure allowlist that governs exactly that. And a
        // CmdExec/PowerShell step runs OS commands by definition. Both require the same explicit
        // opt-in as a dangerous sp_configure option.
        var job = entity.Spec.Job;
        var ownerIsPrivileged = string.IsNullOrWhiteSpace(job.Owner)
                                || Services.SqlServer.SqlIdentifierRules.IsReservedLogin(job.Owner);
        if (ownerIsPrivileged && !job.AllowDangerous)
            return Fail(
                "job.owner is 'sa' (or unset, which defaults to sa): the job's steps then run as sysadmin and can enable " +
                "and call xp_cmdshell, bypassing the sp_configure allowlist. Set job.allowDangerous: true to confirm, " +
                "or give the job a non-privileged owner login.", 422);

        var nonTsqlSteps = job.Steps
            .Where(s => s.Type != null && !s.Type.Equals("TSQL", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();
        if (nonTsqlSteps.Count > 0 && !job.AllowDangerous)
            return Fail(
                $"Step(s) {string.Join(", ", nonTsqlSteps)} use a non-T-SQL subsystem (CmdExec/PowerShell/SSIS/SSAS), which " +
                "executes operating-system commands. Set job.allowDangerous: true to confirm.", 422);

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

        // A GoToStep target is the order of an existing step.
        foreach (var step in entity.Spec.Job.Steps)
            foreach (var target in new[] { step.OnSuccessStep, step.OnFailureStep })
                if (target is { } order && !stepOrders.Contains(order))
                    return Fail($"Step '{step.Name}' jumps to step {order}, which does not exist", 422);

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

            if (schedule.StartTime != null && !System.Text.RegularExpressions.Regex.IsMatch(schedule.StartTime, "^([01][0-9]|2[0-3])[0-5][0-9][0-5][0-9]$"))
                return Fail($"Schedule '{schedule.Name}': startTime must be HHmmss", 422);
            if (schedule.FrequencyType?.ToLowerInvariant() is "weekly" or "monthly" && schedule.RecurrenceFactor is < 1 or > 100)
                return Fail($"Schedule '{schedule.Name}': recurrenceFactor must be 1-100", 422);
            if (schedule.FrequencyType?.ToLowerInvariant() == "daily" && schedule.FrequencyInterval < 1)
                return Fail($"Schedule '{schedule.Name}': frequencyInterval for Daily is the number of days between runs (>= 1)", 422);
            if (schedule.SubdayType is not ("Once" or "Hours" or "Minutes" or "Seconds"))
                return Fail($"Schedule '{schedule.Name}': subdayType must be Once, Hours, Minutes or Seconds", 422);
            var maxSubday = schedule.SubdayType switch { "Hours" => 23, "Minutes" => 1439, "Seconds" => 86399, _ => 0 };
            if (schedule.SubdayType != "Once" && (schedule.SubdayInterval < 1 || schedule.SubdayInterval > maxSubday))
                return Fail($"Schedule '{schedule.Name}': subdayInterval for {schedule.SubdayType} must be 1-{maxSubday}", 422);
            if (schedule.SubdayType == "Seconds" && schedule.SubdayInterval < 10)
                return Fail($"Schedule '{schedule.Name}': SQL Agent runs a schedule at most every 10 seconds", 422);
            if (schedule.StartDate != null && !DateTime.TryParseExact(schedule.StartDate, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return Fail($"Schedule '{schedule.Name}': startDate must be yyyyMMdd", 422);
        }

        return Success();
    }
}
