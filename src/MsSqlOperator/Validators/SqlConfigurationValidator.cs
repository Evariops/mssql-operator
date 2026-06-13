using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlConfiguration))]
public class SqlConfigurationValidator : ValidationWebhook<SqlConfiguration>
{
    public override ValidationResult Create(SqlConfiguration entity, bool dryRun)
    {
        return Validate(entity);
    }

    public override ValidationResult Update(SqlConfiguration oldEntity, SqlConfiguration entity, bool dryRun)
    {
        return Validate(entity);
    }

    private ValidationResult Validate(SqlConfiguration entity)
    {
        var sp = entity.Spec.SpConfigure;
        if (sp == null) return Success();

        if (sp.MaxServerMemoryMb is < 128)
        {
            return Fail("MaxServerMemoryMb must be at least 128 MB", 422);
        }

        if (sp.MaxDegreeOfParallelism is < 0 or > 32767)
        {
            return Fail("MaxDegreeOfParallelism must be between 0 and 32767", 422);
        }

        if (sp.CostThresholdForParallelism is < 0)
        {
            return Fail("CostThresholdForParallelism must be non-negative", 422);
        }

        if (sp.MinServerMemoryMb.HasValue && sp.MaxServerMemoryMb.HasValue &&
            sp.MinServerMemoryMb > sp.MaxServerMemoryMb)
        {
            return Fail("MinServerMemoryMb cannot exceed MaxServerMemoryMb", 422);
        }

        // The typed toggles mapping to dangerous sp_configure options require the same
        // explicit opt-in as advanced[] dangerous entries (enabling only; disabling is always fine).
        foreach (var (field, enabled) in new[]
                 {
                     ("clrEnabled", sp.ClrEnabled),
                     ("crossDbOwnershipChaining", sp.CrossDbOwnershipChaining),
                     ("remoteAdminConnections", sp.RemoteAdminConnections),
                 })
        {
            if (enabled == true && !sp.AllowDangerous)
                return Fail($"spConfigure.{field} enables code execution or crosses a trust boundary; set spConfigure.allowDangerous: true to confirm.", 422);
        }

        // Three-tier allowlist for advanced[]. The same classifier is
        // re-applied at reconcile so the policy holds even if this webhook is disabled.
        foreach (var param in sp.Advanced ?? [])
        {
            switch (Configuration.SpConfigureAllowlist.Classify(param.Name))
            {
                case Configuration.SpConfigureAllowlist.Tier.Safe:
                case Configuration.SpConfigureAllowlist.Tier.Dangerous when param.AllowDangerous:
                    continue;
                case Configuration.SpConfigureAllowlist.Tier.Dangerous:
                    return Fail($"spConfigure.advanced: '{param.Name}' enables code execution or crosses a trust boundary; set allowDangerous: true on this entry to confirm.", 422);
                default:
                    return Fail($"spConfigure.advanced: '{param.Name}' is not in the allowlist. Safe options pass directly; dangerous ones need allowDangerous: true; open an issue to widen the list.", 422);
            }
        }

        return Success();
    }
}
