using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

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

        // Identifiers reaching SQL DDL.
        foreach (var db in entity.Spec.Databases ?? [])
            if (SqlIdentifierRules.Check(db, "databases") is { } dbErr)
                return Fail(dbErr, 422);

        foreach (var proc in entity.Spec.Inline ?? [])
        {
            if (SqlIdentifierRules.Check(proc.Name, "inline.name") is { } nameErr)
                return Fail(nameErr, 422);
            if (SqlIdentifierRules.Check(proc.Schema, "inline.schema") is { } schemaErr)
                return Fail(schemaErr, 422);
        }

        return Success();
    }
}
