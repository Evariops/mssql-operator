using KubeOps.KubernetesClient;
using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroupDatabase))]
public class SqlAvailabilityGroupDatabaseValidator(IKubernetesClient kubernetesClient) : ValidationWebhook<SqlAvailabilityGroupDatabase>
{
    private static readonly string[] SystemDatabases = ["master", "msdb", "model", "tempdb"];
    private static readonly string[] ValidSeedingModes = ["Automatic", "Manual"];

    public override ValidationResult Create(SqlAvailabilityGroupDatabase entity, bool dryRun)
    {
        var result = ValidateSpec(entity);
        if (!result.Valid) return result;

        if (!dryRun)
        {
            var crossResult = ValidateCrossCrdAsync(entity).GetAwaiter().GetResult();
            if (!crossResult.Valid) return crossResult;
        }

        return Success();
    }

    public override ValidationResult Update(SqlAvailabilityGroupDatabase oldEntity, SqlAvailabilityGroupDatabase entity, bool dryRun)
    {
        var result = ValidateSpec(entity);
        if (!result.Valid) return result;

        // Reject changing the AG reference
        if (!string.Equals(oldEntity.Spec.AvailabilityGroupRef.Name, entity.Spec.AvailabilityGroupRef.Name, StringComparison.Ordinal))
            return Fail("availabilityGroupRef cannot be changed. Delete and recreate the resource.", 422);

        // Reject changing the database name
        if (!string.Equals(oldEntity.Spec.DatabaseName, entity.Spec.DatabaseName, StringComparison.OrdinalIgnoreCase))
            return Fail("databaseName cannot be changed. Delete and recreate the resource.", 422);

        return Success();
    }

    internal ValidationResult ValidateSpec(SqlAvailabilityGroupDatabase entity)
    {
        var spec = entity.Spec;

        if (string.IsNullOrWhiteSpace(spec.AvailabilityGroupRef.Name))
            return Fail("availabilityGroupRef.name is required", 422);

        if (string.IsNullOrWhiteSpace(spec.DatabaseName))
            return Fail("databaseName is required", 422);

        if (SystemDatabases.Contains(spec.DatabaseName, StringComparer.OrdinalIgnoreCase))
            return Fail($"System database '{spec.DatabaseName}' cannot be added to an availability group", 422);

        if (!System.Text.RegularExpressions.Regex.IsMatch(spec.DatabaseName, @"^[a-zA-Z_][a-zA-Z0-9_]{0,127}$"))
            return Fail($"databaseName '{spec.DatabaseName}' contains invalid characters. Must match ^[a-zA-Z_][a-zA-Z0-9_]{{0,127}}$", 422);

        if (!ValidSeedingModes.Contains(spec.SeedingMode, StringComparer.OrdinalIgnoreCase))
            return Fail($"seedingMode must be Automatic or Manual. Got: {spec.SeedingMode}", 422);

        return Success();
    }

    private async Task<ValidationResult> ValidateCrossCrdAsync(SqlAvailabilityGroupDatabase entity)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        try
        {
            // Check the referenced AG exists and get its edition
            var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns);
            if (ag == null)
                return Fail($"SqlAvailabilityGroup '{agName}' not found in namespace '{ns}'", 422);

            // List existing SAGDBs in this namespace for this AG
            var existingSagdbs = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null);
            var sagdbsForAg = existingSagdbs
                .Where(s => s.Spec.AvailabilityGroupRef.Name == agName)
                .Where(s => s.Metadata.Uid != entity.Metadata.Uid)
                .ToList();

            // Standard Edition: max 1 database per AG (Basic AG)
            var isStandard = ag.Spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);
            if (isStandard && sagdbsForAg.Count >= 1)
                return Fail($"Standard Edition (Basic AG) allows at most 1 database per AG. AG '{agName}' already has {sagdbsForAg.Count} database(s).", 422);

            // Uniqueness: same database cannot be in the same AG twice
            var duplicate = sagdbsForAg.FirstOrDefault(s =>
                s.Spec.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
                return Fail($"Database '{dbName}' is already referenced by SqlAvailabilityGroupDatabase '{duplicate.Metadata.Name}' in AG '{agName}'", 422);
        }
        catch (Exception)
        {
            // Fail-open: allow through if cross-CRD validation fails
        }

        return Success();
    }
}
