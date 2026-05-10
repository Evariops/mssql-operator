using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlStoredProcedure))]
public class SqlStoredProcedureValidator : ValidationWebhook<SqlStoredProcedure>
{
    public override ValidationResult Create(SqlStoredProcedure entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlStoredProcedure oldEntity, SqlStoredProcedure entity, bool dryRun) => Validate(entity);

    private ValidationResult Validate(SqlStoredProcedure entity)
    {
        var targetError = TargetSelectorValidator.Validate(entity.Spec.Target);
        if (targetError != null) return Fail(targetError, 422);

        if (entity.Spec.Inline == null && entity.Spec.GitHub == null)
            return Fail("At least one source (inline or github) is required", 422);

        return Success();
    }
}
