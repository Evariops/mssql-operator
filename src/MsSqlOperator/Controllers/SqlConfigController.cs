using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
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
    Services.Kubernetes.IEventPublisher eventPublisher) : IEntityController<SqlConfiguration>
{
    public async Task<ReconciliationResult<SqlConfiguration>> ReconcileAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlConfiguration {Name}", entity.Metadata.Name);

        entity.Status ??= new SqlConfigurationStatus();
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Applied", Status = "True",
                Reason = "ConfigurationApplied",
                Message = "Configuration reconciled",
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        await eventPublisher.PublishAsync(entity, "ConfigApplied", $"Configuration {entity.Metadata.Name} reconciled", ct: ct);
        logger.LogInformation("SqlConfiguration {Name} reconciled", entity.Metadata.Name);
        return ReconciliationResult<SqlConfiguration>.Success(entity);
    }

    public Task<ReconciliationResult<SqlConfiguration>> DeletedAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("SqlConfiguration {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlConfiguration>.Success(entity));
    }

    public async Task ApplyToInstanceAsync(SqlConfiguration config, string host, int port, string password, string instanceName, bool instanceHasMemorySpec, CancellationToken ct)
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
            var currentFiles = await sqlServerManager.GetTempDbFileCountAsync(connection, ct);
            while (currentFiles < config.Spec.TempDb.DataFiles)
            {
                await sqlServerManager.AddTempDbFileAsync(connection, config.Spec.TempDb.InitialSizeMb, config.Spec.TempDb.AutoGrowthMb, ct);
                currentFiles++;
                logger.LogInformation("Added TempDB file on {Instance}, now at {Count}", instanceName, currentFiles);
            }
        }

        logger.LogInformation("Configuration applied to {Instance}", instanceName);
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
