using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlAvailabilityGroupDatabase))]
public class SqlAvailabilityGroupDatabaseValidator(
    IKubernetesClient kubernetesClient,
    ILogger<SqlAvailabilityGroupDatabaseValidator> logger) : ValidationWebhook<SqlAvailabilityGroupDatabase>
{
    private static readonly string[] SystemDatabases = ["master", "msdb", "model", "tempdb"];
    private static readonly string[] ValidSeedingModes = ["Automatic", "Manual"];

    // Async path — no GetAwaiter().GetResult() blocking a thread-pool thread inside
    // a webhook.
    public override async Task<ValidationResult> CreateAsync(SqlAvailabilityGroupDatabase entity, bool dryRun, CancellationToken ct)
    {
        var result = ValidateSpec(entity);
        if (!result.Valid) return result;
        if (dryRun) return Success();
        return await ValidateCrossCrdAsync(entity, ct);
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

        // A window that cannot be read would hold the database out of the AG for ever.
        if (spec.Initialization?.BackupWindow is { } window)
        {
            if (!MsSqlOperator.Services.SqlServer.MaintenanceWindows.TryParseStart(window.Start, out _))
                return Fail($"initialization.backupWindow.start must be HH:mm (UTC). Got: {window.Start}", 422);
            if (window.DurationMinutes is < 1 or > 1440)
                return Fail($"initialization.backupWindow.durationMinutes must be between 1 and 1440. Got: {window.DurationMinutes}", 422);
        }

        if (spec.LogBackup is { } logBackup)
        {
            if (logBackup.Mode is not ("Auto" or "Disabled"))
                return Fail($"logBackup.mode must be Auto or Disabled. Got: {logBackup.Mode}", 422);
            if (logBackup.ThresholdPercent is < 10 or > 90)
                return Fail($"logBackup.thresholdPercent must be between 10 and 90. Got: {logBackup.ThresholdPercent}", 422);
            if (logBackup.RetentionHours is < 1 or > 720)
                return Fail($"logBackup.retentionHours must be between 1 and 720. Got: {logBackup.RetentionHours}", 422);
        }

        return Success();
    }

    private async Task<ValidationResult> ValidateCrossCrdAsync(SqlAvailabilityGroupDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var agName = entity.Spec.AvailabilityGroupRef.Name;
        var dbName = entity.Spec.DatabaseName;

        try
        {
            // Check the referenced AG exists and get its edition
            var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
            if (ag == null)
                return Fail($"SqlAvailabilityGroup '{agName}' not found", 422);

            // List existing SAGDBs in this namespace for this AG
            var existingSagdbs = await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct);
            var sagdbsForAg = existingSagdbs
                .Where(s => s.Spec.AvailabilityGroupRef.Name == agName)
                .Where(s => s.Metadata.Uid != entity.Metadata.Uid)
                .ToList();

            // Standard Edition: max 1 database per AG (Basic AG)
            var isStandard = ag.Spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase);
            // The messages name nothing the request does not carry (no count, no other resource).
            if (isStandard && sagdbsForAg.Count >= 1)
                return Fail($"Availability group '{agName}' already holds its database: a Basic availability group (Standard Edition) holds one.", 422);

            // Uniqueness: same database cannot be in the same AG twice
            var duplicate = sagdbsForAg.FirstOrDefault(s =>
                s.Spec.DatabaseName.Equals(dbName, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
                return Fail($"Database '{dbName}' already belongs to availability group '{agName}'.", 422);
        }
        catch (Exception ex)
        {
            // Fail-open stays (availability over enforcement) but is no longer
            // silent — an admission warning + log makes the skipped check visible. The
            // reconcile re-checks uniqueness as the source of authority.
            logger.LogWarning(ex, "Cross-CRD validation skipped for SAGDB {Name}; allowing with warning", entity.Metadata.Name);
            return Success("Cross-resource validation could not run (the referenced AG and database uniqueness were not checked at admission); the reconcile re-validates.");
        }

        return Success();
    }
}
