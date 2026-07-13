using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlUser))]
public class SqlUserValidator : ValidationWebhook<SqlUser>
{
    public override ValidationResult Create(SqlUser entity, bool dryRun) => Validate(entity, isCreate: true);
    public override ValidationResult Update(SqlUser oldEntity, SqlUser entity, bool dryRun) => Validate(entity, isCreate: false);

    private ValidationResult Validate(SqlUser entity, bool isCreate)
    {
        // ── Target ────────────────────────────────────────────
        var targetError = TargetSelectorValidator.Validate(entity.Spec.Target);
        if (targetError != null) return Fail(targetError, 422);

        // ── Login ─────────────────────────────────────────────
        if (entity.Spec.Login == null)
            return Fail("Login spec is required", 422);

        if (SqlIdentifierRules.Check(entity.Spec.Login.Name, "login.name") is { } loginErr)
            return Fail(loginErr, 422);

        // 'sa' (and the operator's own deployer login) must never be managed by a SqlUser
        // — rewriting its password locks the operator out of the instance for good.
        if (SqlIdentifierRules.CheckNotReserved(entity.Spec.Login.Name, "login.name") is { } reservedErr)
            return Fail(reservedErr, 422);

        if (SqlIdentifierRules.CheckOptional(entity.Spec.Login.DefaultDatabase, "login.defaultDatabase") is { } defaultDbErr)
            return Fail(defaultDbErr, 422);

        // ── Roles, permissions, database identifiers ──────────
        if (ValidateGrants(entity.Spec.ServerRoles, entity.Spec.DatabasePermissions, "spec") is { } grantsErr)
            return grantsErr;

        // ── Overrides ─────────────────────────────────────────
        if (entity.Spec.Overrides != null)
        {
            foreach (var o in entity.Spec.Overrides)
            {
                var hasInstance = !string.IsNullOrEmpty(o.Instance);
                var hasAg = !string.IsNullOrEmpty(o.AvailabilityGroup);
                if (hasInstance == hasAg)
                    return Fail("Each override must have exactly one of instance or availabilityGroup", 422);

                if (ValidateGrants(o.ServerRoles, o.DatabasePermissions, $"overrides[{o.Instance ?? o.AvailabilityGroup}]") is { } overrideErr)
                    return overrideErr;
            }
        }

        // ── Expiration ────────────────────────────────────────
        var exp = entity.Spec.Expiration;
        if (exp != null)
        {
            if (!exp.Action.Equals("Disable", StringComparison.OrdinalIgnoreCase) &&
                !exp.Action.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                return Fail("expiration.action must be Disable or Delete", 422);

            var hasExpiresAt = !string.IsNullOrEmpty(exp.ExpiresAt);
            var hasTtl = !string.IsNullOrEmpty(exp.Ttl);

            if (!hasExpiresAt && !hasTtl)
                return Fail("expiration requires either expiresAt or ttl", 422);

            if (hasExpiresAt)
            {
                // RFC3339 with a mandatory timezone — a date without TZ used to be
                // interpreted in the pod's local zone, shifting the effective expiry.
                if (!DateTimeOffset.TryParse(exp.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed)
                    || !(exp.ExpiresAt.EndsWith('Z') || exp.ExpiresAt.Contains('+') || exp.ExpiresAt.LastIndexOf('-') > 7))
                    return Fail($"expiresAt must be an RFC 3339 timestamp with a timezone offset (e.g. 2026-01-01T00:00:00Z): {exp.ExpiresAt}", 422);

                if (isCreate && parsed.ToUniversalTime() <= DateTimeOffset.UtcNow)
                    return Fail("expiresAt must be in the future", 422);
            }

            if (hasTtl)
            {
                var (valid, _, error) = TtlParser.Parse(exp.Ttl!);
                if (!valid)
                    return Fail(error!, 422);
            }
        }

        // ── Privileged roles: admission warning (authoritative check at reconcile) ──
        var allRoles = (entity.Spec.ServerRoles ?? Enumerable.Empty<string>())
            .Concat(entity.Spec.Overrides?.SelectMany(o => o.ServerRoles ?? []) ?? []);
        var privileged = allRoles.Where(r => SqlIdentifierRules.PrivilegedServerRoles.Contains(r)).Distinct().ToList();
        if (privileged.Count > 0)
            return Success($"Privileged server role(s) {string.Join(", ", privileged)} will only be granted if the target " +
                           $"SqlInstance/SqlAvailabilityGroup carries the annotation {SqlIdentifierRules.AllowPrivilegedRolesAnnotation}=\"true\".");

        return Success();
    }

    /// <summary>
    /// Validates serverRoles[] against the fixed-role allowlist and every identifier
    /// reaching SQL DDL against <see cref="SqlIdentifierRules"/>. Privileged roles are
    /// flagged here with a warning only: the authoritative opt-in check (annotation on the target
    /// SqlInstance/AG) runs at reconcile, where targets are actually resolved.
    /// </summary>
    private ValidationResult? ValidateGrants(List<string>? serverRoles, List<DatabasePermissionSpec>? dbPermissions, string path)
    {
        if (serverRoles != null)
        {
            foreach (var role in serverRoles)
            {
                if (!SqlIdentifierRules.FixedServerRoles.Contains(role))
                    return Fail($"{path}.serverRoles: '{role}' is not a fixed SQL Server role. Allowed: {string.Join(", ", SqlIdentifierRules.FixedServerRoles)}", 422);
            }
        }

        if (dbPermissions != null)
        {
            foreach (var perm in dbPermissions)
            {
                foreach (var db in perm.Databases ?? [])
                    if (SqlIdentifierRules.Check(db, $"{path}.databasePermissions.databases") is { } dbErr)
                        return Fail(dbErr, 422);

                foreach (var role in perm.Roles ?? [])
                    if (SqlIdentifierRules.Check(role, $"{path}.databasePermissions.roles") is { } roleErr)
                        return Fail(roleErr, 422);

                foreach (var schemaPerm in perm.SchemaPermissions ?? [])
                    if (SqlIdentifierRules.Check(schemaPerm.Schema, $"{path}.databasePermissions.schemaPermissions.schema") is { } schemaErr)
                        return Fail(schemaErr, 422);
            }
        }

        return null;
    }
}
