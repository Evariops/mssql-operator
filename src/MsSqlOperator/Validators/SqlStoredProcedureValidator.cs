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

        // Sources are exclusive — the combined behaviour was undefined.
        if (entity.Spec.Inline != null && entity.Spec.GitHub != null)
            return Fail("inline and github sources are mutually exclusive", 422);

        if (entity.Spec.GitHub is { } gh)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(gh.Repository ?? "", "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                return Fail("github.repository must be in 'owner/repo' form", 422);

            if (gh.Ref?.Tag != null && gh.Ref?.Sha != null)
                return Fail("github.ref.tag and github.ref.sha are mutually exclusive", 422);

            if (gh.Ref?.Sha is { } sha && !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-fA-F]{40}$"))
                return Fail("github.ref.sha must be a full 40-hex commit SHA (strong pinning)", 422);

            if (entity.Spec.Sync?.FollowLatestRelease == true && (gh.Ref?.Tag != null || gh.Ref?.Sha != null))
                return Fail("sync.followLatestRelease is incompatible with a pinned ref.tag/ref.sha", 422);
        }

        if (entity.Spec.Sync is { IntervalMinutes: < 1 })
            return Fail("sync.intervalMinutes must be >= 1", 422);

        if (entity.Spec.Databases is { Count: > 0 } && entity.Spec.DatabaseFilter != null)
            return Fail("databases and databaseFilter are mutually exclusive: list explicit names or filter, not both", 422);

        // Deploy identity enum.
        if (entity.Spec.DeploymentOptions is { } opts && opts.DeployAs is not ("LeastPrivilege" or "Sa"))
            return Fail("deploymentOptions.deployAs must be LeastPrivilege or Sa", 422);

        foreach (var proc in entity.Spec.Inline ?? [])
            if (string.IsNullOrWhiteSpace(proc.Definition))
                return Fail($"inline procedure '{proc.Name}' has an empty definition", 422);

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
