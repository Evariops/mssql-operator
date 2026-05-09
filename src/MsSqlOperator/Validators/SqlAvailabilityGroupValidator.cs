using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroup))]
public class SqlAvailabilityGroupValidator : ValidationWebhook<SqlAvailabilityGroup>
{
    public override ValidationResult Create(SqlAvailabilityGroup entity, bool dryRun)
    {
        return Validate(entity);
    }

    public override ValidationResult Update(SqlAvailabilityGroup oldEntity, SqlAvailabilityGroup entity, bool dryRun)
    {
        return Validate(entity);
    }

    private ValidationResult Validate(SqlAvailabilityGroup entity)
    {
        var tmpl = entity.Spec.InstanceTemplate;
        if (tmpl != null && !SqlServerImages.IsAgCompatible(tmpl.Edition))
        {
            return Fail($"Availability Groups require Enterprise or Developer edition, got: {tmpl.Edition}", 422);
        }

        if (entity.Spec.Replicas < 2 || entity.Spec.Replicas > 9)
        {
            return Fail("Replicas must be between 2 and 9", 422);
        }

        if (entity.Spec.SynchronousReplicas > entity.Spec.Replicas)
        {
            return Fail("SynchronousReplicas cannot exceed Replicas", 422);
        }

        return Success();
    }
}
