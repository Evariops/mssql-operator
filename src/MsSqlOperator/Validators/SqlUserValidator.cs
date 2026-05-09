using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlUser))]
public class SqlUserValidator : ValidationWebhook<SqlUser>
{
    public override ValidationResult Create(SqlUser entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlUser oldEntity, SqlUser entity, bool dryRun) => Validate(entity);

    private ValidationResult Validate(SqlUser entity)
    {
        if (entity.Spec.InstanceRef == null && entity.Spec.AvailabilityGroupRef == null)
        {
            return Fail("Either instanceRef or availabilityGroupRef must be specified", 422);
        }

        if (entity.Spec.Login == null)
        {
            return Fail("Login spec is required", 422);
        }

        if (string.IsNullOrWhiteSpace(entity.Spec.Login.Name))
        {
            return Fail("Login name is required", 422);
        }

        return Success();
    }
}
