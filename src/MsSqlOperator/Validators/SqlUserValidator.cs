using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

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

        if (string.IsNullOrWhiteSpace(entity.Spec.Login.Name))
            return Fail("Login name is required", 422);

        // ── Overrides ─────────────────────────────────────────
        if (entity.Spec.Overrides != null)
        {
            foreach (var o in entity.Spec.Overrides)
            {
                var hasInstance = !string.IsNullOrEmpty(o.Instance);
                var hasAg = !string.IsNullOrEmpty(o.AvailabilityGroup);
                if (hasInstance == hasAg)
                    return Fail("Each override must have exactly one of instance or availabilityGroup", 422);
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
                if (!DateTime.TryParse(exp.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    return Fail($"expiresAt is not a valid RFC 3339 date: {exp.ExpiresAt}", 422);

                if (isCreate && parsed.ToUniversalTime() <= DateTime.UtcNow)
                    return Fail("expiresAt must be in the future", 422);
            }

            if (hasTtl)
            {
                var (valid, _, error) = TtlParser.Parse(exp.Ttl!);
                if (!valid)
                    return Fail(error!, 422);
            }
        }

        return Success();
    }
}
