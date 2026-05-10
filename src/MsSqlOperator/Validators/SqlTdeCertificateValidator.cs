using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlTdeCertificate))]
public class SqlTdeCertificateValidator : ValidationWebhook<SqlTdeCertificate>
{
    public override ValidationResult Create(SqlTdeCertificate entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlTdeCertificate oldEntity, SqlTdeCertificate entity, bool dryRun) => Validate(entity);

    private ValidationResult Validate(SqlTdeCertificate entity)
    {
        var targetError = TargetSelectorValidator.Validate(entity.Spec.Target);
        if (targetError != null) return Fail(targetError, 422);

        if (entity.Spec.Certificate == null)
            return Fail("certificate spec is required", 422);

        if (string.IsNullOrWhiteSpace(entity.Spec.Certificate.Name))
            return Fail("certificate.name is required", 422);

        return Success();
    }
}
