using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlConfiguration), Verbs = RbacVerb.All)]
public class SqlConfigController(
    ILogger<SqlConfigController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IDatabaseMailManager databaseMailManager,
    Services.Metrics.OperatorMetrics metrics,
    Services.Kubernetes.IEventPublisher eventPublisher) : IEntityController<SqlConfiguration>
{
    public async Task<ReconciliationResult<SqlConfiguration>> ReconcileAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlConfiguration {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var appliedInstances = new List<string>();
        var errors = new List<string>();

        // Resolve target instances by labels
        var targetInstances = await ResolveTargetInstancesAsync(entity, ns, ct);

        foreach (var instance in targetInstances)
        {
            try
            {
                var saPassword = await ResolveSaPasswordAsync(instance, ns, ct);
                if (saPassword == null)
                {
                    errors.Add($"{instance.Metadata.Name}: SA password not found");
                    continue;
                }

                var serviceSpec = instance.Spec.Service ?? new ServiceSpec();
                var host = $"{instance.Metadata.Name}-0.{instance.Metadata.Name}.{ns}.svc.cluster.local";
                var hasTempDbVolume = instance.Spec.Storage?.TempDb != null;
                await ApplyToInstanceAsync(entity, host, serviceSpec.Port, saPassword,
                    instance.Metadata.Name, instance.Spec.Memory != null, hasTempDbVolume, ct);
                appliedInstances.Add(instance.Metadata.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply config to {Instance}", instance.Metadata.Name);
                errors.Add($"{instance.Metadata.Name}: {ex.Message}");
            }
        }

        // Update status (slim): counts + condition only; the per-instance applied list is
        // emitted as Events, not stored in etcd.
        var matched = targetInstances.Count;
        entity.Status ??= new SqlConfigurationStatus();
        // Capture prior roll-ups before overwriting so we only re-stamp LastAppliedTime on a real change.
        // sp_configure is applied idempotently every reconcile; stamping the time unconditionally would
        // make the status byte-unstable, defeating the dedup and causing a self-reconcile hot loop.
        var prevGen = entity.Status.ObservedGeneration;
        var prevApplied = entity.Status.AppliedInstances;
        var prevMatched = entity.Status.MatchedInstances;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Phase = errors.Count == 0 ? (matched == 0 ? "Pending" : "Applied") : "PartiallyApplied";
        entity.Status.MatchedInstances = matched;
        entity.Status.AppliedInstances = appliedInstances.Count;
        var appliedChanged = prevGen != entity.Status.ObservedGeneration
            || prevApplied != appliedInstances.Count
            || prevMatched != matched;
        if (appliedInstances.Count > 0 && (appliedChanged || entity.Status.LastAppliedTime is null))
            entity.Status.LastAppliedTime = DateTime.UtcNow;
        ConditionOps.Set(entity.Status.Conditions ??= [], "Applied",
            errors.Count == 0 ? "True" : "False",
            errors.Count == 0 ? "ConfigurationApplied" : "PartiallyApplied",
            errors.Count == 0
                ? $"Applied to {appliedInstances.Count} instance(s)"
                : $"Applied to {appliedInstances.Count}, failed on {errors.Count}: {string.Join("; ", errors)}");

        foreach (var inst in appliedInstances)
            await eventPublisher.PublishAsync(entity, "ConfigurationApplied", $"Applied to instance {inst}", ct: ct);
        metrics.SetTargets("SqlConfiguration", ns, entity.Metadata.Name, appliedInstances.Count, matched, errors.Count);
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        logger.LogInformation("SqlConfiguration {Name} reconciled ({Applied} applied, {Errors} errors)",
            entity.Metadata.Name, appliedInstances.Count, errors.Count);
        return ReconciliationResult<SqlConfiguration>.Success(entity);
    }

    public Task<ReconciliationResult<SqlConfiguration>> DeletedAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("SqlConfiguration {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlConfiguration", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlConfiguration>.Success(entity));
    }

    public async Task ApplyToInstanceAsync(SqlConfiguration config, string host, int port, string password, string instanceName, bool instanceHasMemorySpec, bool hasTempDbVolume, CancellationToken ct)
    {
        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        var currentConfig = await sqlServerManager.GetCurrentConfigAsync(connection, ct);
        var spec = config.Spec.SpConfigure;

        if (spec != null)
        {
            // When the instance uses spec.memory, skip memory params from SqlConfiguration
            if (instanceHasMemorySpec)
            {
                if (spec.MaxServerMemoryMb.HasValue || spec.MinServerMemoryMb.HasValue)
                {
                    logger.LogWarning("SqlConfiguration {Config} defines memory params but instance {Instance} uses spec.memory — ignoring maxServerMemoryMb/minServerMemoryMb",
                        config.Metadata.Name, instanceName);
                    await eventPublisher.PublishAsync(config, "MemoryConfigConflict",
                        $"maxServerMemoryMb/minServerMemoryMb ignored for instance '{instanceName}' (uses spec.memory)", "Warning", ct);
                }
            }
            else
            {
                await ApplyIfDifferent(connection, currentConfig, "max server memory (MB)", spec.MaxServerMemoryMb, ct);
                await ApplyIfDifferent(connection, currentConfig, "min server memory (MB)", spec.MinServerMemoryMb, ct);
            }

            await ApplyIfDifferent(connection, currentConfig, "max degree of parallelism", spec.MaxDegreeOfParallelism, ct);
            await ApplyIfDifferent(connection, currentConfig, "cost threshold for parallelism", spec.CostThresholdForParallelism, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "optimize for ad hoc workloads", spec.OptimizeForAdHocWorkloads, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "remote admin connections", spec.RemoteAdminConnections, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "backup compression default", spec.BackupCompressionDefault, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "contained database authentication", spec.ContainedDatabaseAuthentication, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "clr enabled", spec.ClrEnabled, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "cross db ownership chaining", spec.CrossDbOwnershipChaining, ct);

            if (spec.Advanced != null)
            {
                foreach (var param in spec.Advanced)
                {
                    await ApplyIfDifferent(connection, currentConfig, param.Name, param.Value, ct);
                }
            }

            await sqlServerManager.ReconfigureAsync(connection, ct: ct);
        }

        // TraceFlags
        if (config.Spec.TraceFlags?.Global != null)
        {
            var activeFlags = await sqlServerManager.GetActiveTraceFlagsAsync(connection, ct);
            foreach (var flag in config.Spec.TraceFlags.Global.Except(activeFlags))
            {
                await sqlServerManager.EnableTraceFlagAsync(connection, flag, ct);
                logger.LogInformation("Enabled traceflag {Flag} on {Instance}", flag, instanceName);
            }
            foreach (var flag in activeFlags.Except(config.Spec.TraceFlags.Global))
            {
                await sqlServerManager.DisableTraceFlagAsync(connection, flag, ct);
                logger.LogInformation("Disabled traceflag {Flag} on {Instance}", flag, instanceName);
            }
        }

        // TempDB files
        if (config.Spec.TempDb != null)
        {
            var tempDbPath = hasTempDbVolume ? "/var/opt/mssql/tempdb" : "/var/opt/mssql/data";

            // Relocate existing tempdb files to dedicated volume if needed
            if (hasTempDbVolume)
            {
                var fileLocations = await sqlServerManager.GetTempDbFileLocationsAsync(connection, ct);
                foreach (var (name, fileName) in fileLocations)
                {
                    if (!fileName.StartsWith(tempDbPath))
                    {
                        var newFileName = $"{tempDbPath}/{Path.GetFileName(fileName)}";
                        await sqlServerManager.RelocateTempDbFileAsync(connection, name, newFileName, ct);
                        logger.LogInformation("Relocated TempDB file {Name} from {Old} to {New} on {Instance} (requires restart)",
                            name, fileName, newFileName, instanceName);
                    }
                }
            }

            var currentFiles = await sqlServerManager.GetTempDbFileCountAsync(connection, ct);
            while (currentFiles < config.Spec.TempDb.DataFiles)
            {
                await sqlServerManager.AddTempDbFileAsync(connection, config.Spec.TempDb.InitialSizeMb, config.Spec.TempDb.AutoGrowthMb, tempDbPath, ct);
                currentFiles++;
                logger.LogInformation("Added TempDB file on {Instance}, now at {Count}", instanceName, currentFiles);
            }
        }

        // Database Mail
        if (config.Spec.DatabaseMail?.Enabled == true)
        {
            var configNs = config.Metadata.NamespaceProperty;
            async Task<string?> ResolveSmtpSecret(SecretReference secretRef, CancellationToken token)
            {
                var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, configNs, token);
                if (secret?.Data == null || !secret.Data.TryGetValue(secretRef.Key, out var bytes))
                    return null;
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            await databaseMailManager.ConfigureAsync(connection, config.Spec.DatabaseMail, ResolveSmtpSecret, ct);
            logger.LogInformation("Database Mail configured on {Instance}", instanceName);
        }

        // Agent Operators
        if (config.Spec.AgentOperators != null)
        {
            await databaseMailManager.ConfigureOperatorsAsync(connection, config.Spec.AgentOperators, ct);
            logger.LogInformation("Agent operators configured on {Instance}", instanceName);
        }

        logger.LogInformation("Configuration applied to {Instance}", instanceName);
    }

    private async Task<List<SqlInstance>> ResolveTargetInstancesAsync(SqlConfiguration config, string ns, CancellationToken ct)
    {
        var allInstances = await kubernetesClient.ListAsync<SqlInstance>(ns, null, ct);
        var targets = new List<SqlInstance>();

        foreach (var instance in allInstances)
        {
            // Match by configurationRef
            if (instance.Spec.ConfigurationRef?.Name == config.Metadata.Name)
            {
                targets.Add(instance);
                continue;
            }

            // Match by targetMatchLabels
            if (config.Spec.TargetMatchLabels != null && instance.Metadata.Labels != null)
            {
                var allMatch = config.Spec.TargetMatchLabels.All(kv =>
                    instance.Metadata.Labels.TryGetValue(kv.Key, out var val) && val == kv.Value);
                if (allMatch)
                {
                    targets.Add(instance);
                }
            }
        }

        return targets;
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlInstance instance, string ns, CancellationToken ct)
    {
        if (instance.Spec.SaPasswordSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(instance.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(instance.Spec.SaPasswordSecretRef.Key, out var bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }

    private async Task ApplyIfDifferent(Microsoft.Data.SqlClient.SqlConnection connection, Dictionary<string, int> current, string name, int? desired, CancellationToken ct)
    {
        if (desired == null) return;
        if (current.TryGetValue(name, out var currentValue) && currentValue == desired.Value) return;

        await sqlServerManager.ApplySpConfigureAsync(connection, name, desired.Value, ct);
        logger.LogInformation("Applied sp_configure '{Name}' = {Value}", name, desired.Value);
    }

    private async Task ApplyBoolIfDifferent(Microsoft.Data.SqlClient.SqlConnection connection, Dictionary<string, int> current, string name, bool? desired, CancellationToken ct)
    {
        if (desired == null) return;
        await ApplyIfDifferent(connection, current, name, desired.Value ? 1 : 0, ct);
    }
}
