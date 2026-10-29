using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlDatabase))]
public partial class SqlDatabaseValidator(
    IKubernetesClient kubernetesClient,
    ILogger<SqlDatabaseValidator> logger) : ValidationWebhook<SqlDatabase>
{
    private static readonly string[] SystemDatabases = ["master", "msdb", "model", "tempdb"];
    private static readonly string[] RecoveryModels = ["Full", "Simple", "BulkLogged"];
    private static readonly int[] CompatibilityLevels = [100, 110, 120, 130, 140, 150, 160, 170];

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]{0,127}$")]
    private static partial Regex DatabaseName();

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex CollationName();

    [GeneratedRegex(@"^([1-9][0-9]?|100)%$")]
    private static partial Regex Percent();

    public override async Task<ValidationResult> CreateAsync(SqlDatabase entity, bool dryRun, CancellationToken ct)
    {
        var result = ValidateSpec(entity);
        if (!result.Valid || dryRun) return result;
        return await ValidateCrossResourceAsync(entity, ct);
    }

    public override ValidationResult Update(SqlDatabase oldEntity, SqlDatabase entity, bool dryRun)
    {
        var result = ValidateSpec(entity);
        if (!result.Valid) return result;

        if (oldEntity.Spec.InstanceRef?.Name != entity.Spec.InstanceRef?.Name
            || oldEntity.Spec.AvailabilityGroupRef?.Name != entity.Spec.AvailabilityGroupRef?.Name)
            return Fail("instanceRef and availabilityGroupRef cannot be changed. Delete and recreate the resource.", 422);
        if (!string.Equals(oldEntity.Spec.DatabaseName, entity.Spec.DatabaseName, StringComparison.OrdinalIgnoreCase))
            return Fail("databaseName cannot be changed. Delete and recreate the resource.", 422);
        if (!string.Equals(oldEntity.Spec.Collation, entity.Spec.Collation, StringComparison.OrdinalIgnoreCase))
            return Fail("collation cannot be changed: a database keeps the collation it was created with.", 422);
        return result;
    }

    internal ValidationResult ValidateSpec(SqlDatabase entity)
    {
        var spec = entity.Spec;
        var onInstance = !string.IsNullOrWhiteSpace(spec.InstanceRef?.Name);
        var onAg = !string.IsNullOrWhiteSpace(spec.AvailabilityGroupRef?.Name);
        if (onInstance == onAg)
            return Fail("Set exactly one of instanceRef.name and availabilityGroupRef.name.", 422);

        if (string.IsNullOrWhiteSpace(spec.DatabaseName))
            return Fail("databaseName is required", 422);
        if (SystemDatabases.Contains(spec.DatabaseName, StringComparer.OrdinalIgnoreCase))
            return Fail($"'{spec.DatabaseName}' is a system database.", 422);
        if (!DatabaseName().IsMatch(spec.DatabaseName))
            return Fail($"databaseName '{spec.DatabaseName}' must match ^[a-zA-Z_][a-zA-Z0-9_]{{0,127}}$", 422);

        if (spec.Collation is { } collation && !CollationName().IsMatch(collation))
            return Fail($"collation '{collation}' is not a collation name (letters, digits and underscores).", 422);

        if (spec.RecoveryModel is { } model)
        {
            if (!RecoveryModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                return Fail($"recoveryModel must be Full, Simple or BulkLogged. Got: {model}", 422);
            if (onAg && !model.Equals("Full", StringComparison.OrdinalIgnoreCase))
                return Fail("recoveryModel must be Full for a database in an availability group.", 422);
        }

        if (spec.CompatibilityLevel is { } level && !CompatibilityLevels.Contains(level))
            return Fail($"compatibilityLevel must be one of {string.Join(", ", CompatibilityLevels)}. Got: {level}", 422);

        if (spec.Owner is { } owner && SqlIdentifierRules.Check(owner, "owner") is { } ownerError)
            return Fail(ownerError, 422);

        foreach (var (file, label) in new[] { (spec.Files?.Data, "files.data"), (spec.Files?.Log, "files.log") })
            if (file is not null && FileError(file, label) is { } fileError)
                return Fail(fileError, 422);

        if (spec.DeletionPolicy is not ("Retain" or "Delete"))
            return Fail($"deletionPolicy must be Retain or Delete. Got: {spec.DeletionPolicy}", 422);

        if (spec.AvailabilityGroup is { } ag)
        {
            if (!onAg)
                return Fail("availabilityGroup applies only with availabilityGroupRef.", 422);
            // The block is applied to a SqlAvailabilityGroupDatabase: the same rules.
            var probe = new SqlAvailabilityGroupDatabase
            {
                Spec = new SqlAvailabilityGroupDatabaseSpec
                {
                    AvailabilityGroupRef = new() { Name = spec.AvailabilityGroupRef!.Name },
                    DatabaseName = spec.DatabaseName,
                    SeedingMode = ag.SeedingMode ?? "Automatic",
                    LogBackup = ag.LogBackup,
                    Initialization = ag.Initialization,
                    DataMovement = ag.DataMovement,
                },
            };
            var agResult = new SqlAvailabilityGroupDatabaseValidator(kubernetesClient,
                NullLogger<SqlAvailabilityGroupDatabaseValidator>.Instance).ValidateSpec(probe);
            if (!agResult.Valid)
                return Fail($"availabilityGroup: {agResult.Message}", 422);
        }

        return Success();
    }

    private static string? FileError(SqlDatabaseFileSpec file, string label)
    {
        if (file.Size is { } size && (Bytes(size) is not { } bytes || bytes < 1L << 20))
            return $"{label}.size must be a quantity of at least 1Mi. Got: {size}";
        if (file.Growth is { } growth && !Percent().IsMatch(growth) && (Bytes(growth) is not { } g || g < 64L << 10))
            return $"{label}.growth must be a quantity of at least 64Ki, or a percentage (1% to 100%). Got: {growth}";
        if (file.MaxSize is { } max && !max.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)
            && (Bytes(max) is not { } m || m < 1L << 20 || (file.Size is { } s && Bytes(s) is { } sb && m < sb)))
            return $"{label}.maxSize must be Unlimited, or a quantity of at least 1Mi and at least size. Got: {max}";
        return null;
    }

    internal static long? Bytes(string quantity)
    {
        try
        {
            return (long)new k8s.Models.ResourceQuantity(quantity).ToDecimal();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private async Task<ValidationResult> ValidateCrossResourceAsync(SqlDatabase entity, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var spec = entity.Spec;
        try
        {
            var siblings = (await kubernetesClient.ListAsync<SqlDatabase>(ns, null, ct))
                .Where(d => d.Metadata.Uid != entity.Metadata.Uid).ToList();
            if (spec.InstanceRef is { Name: { Length: > 0 } instanceName })
            {
                if (await kubernetesClient.GetAsync<SqlInstance>(instanceName, ns, ct) is null)
                    return Fail($"SqlInstance '{instanceName}' not found", 422);
                if (siblings.Any(d => d.Spec.InstanceRef?.Name == instanceName
                                      && d.Spec.DatabaseName.Equals(spec.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                    return Fail($"Database '{spec.DatabaseName}' of SqlInstance '{instanceName}' is already declared by another SqlDatabase.", 422);
            }
            else
            {
                var agName = spec.AvailabilityGroupRef!.Name;
                var ag = await kubernetesClient.GetAsync<SqlAvailabilityGroup>(agName, ns, ct);
                if (ag is null)
                    return Fail($"SqlAvailabilityGroup '{agName}' not found", 422);
                var onThisAg = siblings.Where(d => d.Spec.AvailabilityGroupRef?.Name == agName).ToList();
                if (onThisAg.Any(d => d.Spec.DatabaseName.Equals(spec.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                    return Fail($"Database '{spec.DatabaseName}' of availability group '{agName}' is already declared by another SqlDatabase.", 422);
                // A Basic availability group (Standard edition) holds one database.
                if (ag.Spec.Edition.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                {
                    var joined = (await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct))
                        .Where(d => d.Spec.AvailabilityGroupRef.Name == agName
                                    && !d.Spec.DatabaseName.Equals(spec.DatabaseName, StringComparison.OrdinalIgnoreCase));
                    if (onThisAg.Count > 0 || joined.Any())
                        return Fail($"Availability group '{agName}' already holds its database: a Basic availability group (Standard Edition) holds one.", 422);
                }
            }
        }
        catch (Exception ex)
        {
            // Fail-open, as the other cross-resource checks: the reconcile checks ownership on the
            // server itself, which is the authority.
            logger.LogWarning(ex, "Cross-resource validation skipped for SqlDatabase {Name}; allowing with warning", entity.Metadata.Name);
            return Success("Cross-resource validation could not run (the target and the uniqueness of the database were not checked at admission); the reconcile checks the ownership on the server.");
        }
        return Success();
    }
}
