using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Finalizers;

/// <summary>
/// Deleting a SqlConfiguration left its sp_configure values and trace flags active — and
/// the record of which flags it owned went away with the CR, so nothing could ever turn them off.
/// The finalizer restores, on every instance it changed, the values it found and turns off the
/// flags it turned on. And forgets the msdb records of what it owned — left
/// behind, they still named the deleted configuration as the owner of a flag it no longer held.
/// </summary>
public class SqlConfigurationFinalizer(
    ILogger<SqlConfigurationFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IEventPublisher eventPublisher,
    ISqlOwnershipRegistry? ownership = null) : IEntityFinalizer<SqlConfiguration>
{
    public string Identifier => "mssql-operator.evariops.com/sqlconfiguration-finalizer";

    public async Task<ReconciliationResult<SqlConfiguration>> FinalizeAsync(SqlConfiguration entity, CancellationToken ct)
    {
        var ledger = ConfigurationLedger.Load(entity);
        if (ledger.IsEmpty)
            return ReconciliationResult<SqlConfiguration>.Success(entity);

        var ns = entity.Metadata.NamespaceProperty;
        var instances = await kubernetesClient.ListAsync<SqlInstance>(ns, null, ct);
        var ags = await kubernetesClient.ListAsync<SqlAvailabilityGroup>(ns, null, ct);
        var everyTarget = ConfigTarget.Build(instances, ags, ns);
        ledger.AdoptReplicaKeys(everyTarget);
        var index = everyTarget.ToDictionary(t => t.Key, StringComparer.Ordinal);

        foreach (var instance in ledger.Instances.ToList())
        {
            if (!index.TryGetValue(instance, out var target))
                continue; // the instance is gone, and its settings with it

            try
            {
                var saPassword = await SecretValues.TryGetAsync(kubernetesClient, target.SaPasswordRef, ns, ct)
                    ?? throw new InvalidOperationException($"SA password of {target.Owner} not found");
                await using var connection = await connectionFactory.CreateConnectionAsync(target.Host, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                await ledger.RevertAsync(sqlServerManager, connection, target, ct);
                if (ownership is not null && entity.Metadata.Uid is { Length: > 0 } uid)
                {
                    var owner = new ResourceOwner("SqlConfiguration", ns, entity.Metadata.Name, uid);
                    foreach (var setting in await ownership.GetOwnedSettingsAsync(connection, owner, ct))
                        await ownership.ForgetSettingAsync(connection, setting.Setting, owner, ct);
                    foreach (var flag in await ownership.GetOwnedTraceFlagsAsync(connection, owner, ct))
                        await ownership.ForgetTraceFlagAsync(connection, flag, owner, ct);
                }
                logger.LogInformation("SqlConfiguration {Name}: restored the settings it changed on {Instance}", entity.Metadata.Name, target.Instance);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "SqlConfiguration {Name}: could not restore the settings it changed on {Instance}", entity.Metadata.Name, instance);
                var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/force-finalize", out var force) == true
                                    && force == "true";
                if (forceFinalize)
                {
                    await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                        $"Settings changed on {instance} were not restored (force-finalize): {ex.Message}", "Warning", ct);
                    continue;
                }

                return ReconciliationResult<SqlConfiguration>.Failure(entity,
                    $"Could not restore the settings changed on {instance}: {ex.Message}. Set the force-finalize annotation to give up.");
            }
        }

        return ReconciliationResult<SqlConfiguration>.Success(entity);
    }
}
