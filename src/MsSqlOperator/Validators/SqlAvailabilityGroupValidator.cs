using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroup))]
public class SqlAvailabilityGroupValidator : ValidationWebhook<SqlAvailabilityGroup>
{
    private static readonly string[] SystemDatabases = ["master", "msdb", "model", "tempdb"];

    public override ValidationResult Create(SqlAvailabilityGroup entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlAvailabilityGroup oldEntity, SqlAvailabilityGroup entity, bool dryRun) => Validate(entity);

    private ValidationResult Validate(SqlAvailabilityGroup entity)
    {
        var spec = entity.Spec;
        var isStandard = spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);
        var isEnterprise = spec.Edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
                        || spec.Edition.Equals("Developer", StringComparison.OrdinalIgnoreCase);

        if (!isStandard && !isEnterprise)
            return Fail($"Edition must be Standard, Enterprise, or Developer. Got: {spec.Edition}", 422);

        // ── Memory validation ─────────────────────────────────
        var tmpl = spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        if (tmpl.Memory != null)
        {
            var (valid, message) = MemorySpecValidator.Validate(tmpl.Memory);
            if (!valid) return Fail(message!, 422);
        }

        // ── Common rules ──────────────────────────────────────
        if (spec.SaPasswordSecretRef == null)
            return Fail("saPasswordSecretRef is required", 422);

        if (spec.MasterKeyPasswordSecretRef == null)
            return Fail("masterKeyPasswordSecretRef is required", 422);

        if (spec.HadrCertificate == null)
            return Fail("hadrCertificate is required", 422);

        // Only the Generate/PerReplica flow is implemented by the controller today.
        // Reject the documented-but-unimplemented combinations rather than silently
        // ignoring them (they would otherwise behave as Generate/PerReplica).
        if (!spec.HadrCertificate.Source.Equals("Generate", StringComparison.OrdinalIgnoreCase))
            return Fail($"hadrCertificate.source '{spec.HadrCertificate.Source}' is not supported yet; only 'Generate' is implemented", 422);

        if (!spec.HadrCertificate.Model.Equals("PerReplica", StringComparison.OrdinalIgnoreCase))
            return Fail($"hadrCertificate.model '{spec.HadrCertificate.Model}' is not supported yet; only 'PerReplica' is implemented", 422);

        var endpoint = spec.Endpoint ?? new AgEndpointSpec();
        if (endpoint.Port is < 1024 or > 65535)
            return Fail("endpoint.port must be between 1024 and 65535", 422);

        if (spec.Listener != null && spec.Listener.Port is < 1024 or > 65535)
            return Fail("listener.port must be between 1024 and 65535", 422);

        if (spec.Listener != null && spec.Listener.Port == endpoint.Port)
            return Fail("listener.port must differ from endpoint.port", 422);

        var failover = spec.Failover ?? new AgFailoverSpec();
        if (failover.HealthCheckTimeoutSeconds < 5)
            return Fail("failover.healthCheckTimeoutSeconds must be >= 5", 422);

        if (failover.FailoverConditionLevel is < 1 or > 5)
            return Fail("failover.failoverConditionLevel must be between 1 and 5", 422);

        // Databases are now managed by SqlAvailabilityGroupDatabase CRDs — no database validation here

        // Read-scale AGs (synchronousCount == 0) are valid with rsstc == 0; the old
        // check (rsstc >= syncCount → 0 >= 0) rejected them systematically.
        var rsstc = spec.Replicas.RequiredSynchronizedSecondariesToCommit;
        var syncCount = spec.Replicas.SynchronousCount;
        if (!(rsstc == 0 && syncCount == 0) && rsstc > Math.Max(0, syncCount - 1))
            return Fail("requiredSynchronizedSecondariesToCommit must be <= synchronousCount - 1 (or 0 for a read-scale AG with synchronousCount 0)", 422);

        if (spec.Replicas.SynchronousCount > spec.Replicas.Count)
            return Fail("synchronousCount cannot exceed replica count", 422);

        // ── Standard-specific rules ───────────────────────────
        if (isStandard)
        {
            if (spec.Replicas.Count != 2)
                return Fail("Standard Edition requires exactly 2 replicas", 422);

            // Database count per AG is enforced by SqlAvailabilityGroupDatabaseValidator

            if (spec.ReadOnlyListener != null)
                return Fail("ReadOnlyListener is not supported in Standard Edition", 422);

            if (spec.ReadOnlyRouting is { Enabled: true })
                return Fail("Read-Only Routing is not supported in Standard Edition", 422);

            if (spec.BackupPreference != null)
                return Fail("BackupPreference is not supported in Standard Edition", 422);

            // Overrides: no async, no readable secondary
            if (spec.Replicas.Overrides != null)
            {
                foreach (var ov in spec.Replicas.Overrides)
                {
                    if (ov.AvailabilityMode?.Equals("AsynchronousCommit", StringComparison.OrdinalIgnoreCase) == true)
                        return Fail("Standard Edition does not support AsynchronousCommit replicas", 422);

                    if (ov.ReadableSecondary != null && !ov.ReadableSecondary.Equals("No", StringComparison.OrdinalIgnoreCase))
                        return Fail("Standard Edition does not support readable secondaries", 422);

                    if (ov.BackupPriority.HasValue)
                        return Fail("BackupPriority is not supported in Standard Edition", 422);
                }
            }

            // This used to be an empty block — replicaDefaults.readableSecondary was
            // silently accepted for Standard while per-replica overrides were rejected.
            if (spec.ReplicaDefaults?.ReadableSecondary != null
                && !spec.ReplicaDefaults.ReadableSecondary.Equals("No", StringComparison.OrdinalIgnoreCase)
                && !spec.ReplicaDefaults.ReadableSecondary.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Standard Edition does not support readable secondaries (replicaDefaults.readableSecondary must be 'No' or omitted; 'All' is tolerated and overridden to No)", 422);
            }
        }

        // ── Enterprise-specific rules ─────────────────────────
        if (isEnterprise)
        {
            if (spec.Replicas.Count is < 2 or > 9)
                return Fail("Enterprise Edition supports 2-9 replicas", 422);
        }

        return Success();
    }
}
