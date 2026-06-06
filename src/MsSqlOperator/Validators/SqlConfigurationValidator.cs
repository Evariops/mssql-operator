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

        return Success();
    }
}
