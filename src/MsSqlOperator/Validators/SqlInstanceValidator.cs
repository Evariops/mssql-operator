using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlInstance))]
public class SqlInstanceValidator : ValidationWebhook<SqlInstance>
{
    public override ValidationResult Create(SqlInstance entity, bool dryRun)
    {
        return Validate(entity);
    }

    public override ValidationResult Update(SqlInstance oldEntity, SqlInstance entity, bool dryRun)
    {
        var result = Validate(entity);
        if (!result.Valid) return result;

        // Reject edition changes
        if (!string.Equals(oldEntity.Spec.Edition, entity.Spec.Edition, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Edition change is not supported in-place. Create a new instance instead.", 422);
        }

        // Reject version downgrades
        if (oldEntity.Spec.Version != entity.Spec.Version &&
            !SqlServerImages.IsValidUpgrade(oldEntity.Spec.Version, entity.Spec.Version))
        {
            return Fail($"Version downgrade from {oldEntity.Spec.Version} to {entity.Spec.Version} is not supported.", 422);
        }

        return Success();
    }

    private ValidationResult Validate(SqlInstance entity)
    {
        if (!SqlServerImages.IsValidCombination(entity.Spec.Version, entity.Spec.Edition))
        {
            return Fail($"Invalid version/edition combination: {entity.Spec.Version}/{entity.Spec.Edition}", 422);
        }

        if (entity.Spec.Mode != "SingleNode" && entity.Spec.Mode != "AvailabilityGroupMember")
        {
            return Fail("Mode must be 'SingleNode' or 'AvailabilityGroupMember'", 422);
        }

        return Success();
    }
}
