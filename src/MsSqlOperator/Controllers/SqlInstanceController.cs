using k8s;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlInstance), Verbs = RbacVerb.All)]
public class SqlInstanceController(
    ILogger<SqlInstanceController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder,
    IMssqlConfBuilder mssqlConfBuilder,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager) : IEntityController<SqlInstance>
{
    public async Task<ReconciliationResult<SqlInstance>> ReconcileAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlInstance {Name} in {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // Resolve SA password (still needed for operator SQL connections)
        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for {Name}", name);
            entity = await UpdateStatus(entity, "Failed", "SaPasswordNotFound", "SA password secret not found", ct);
            return ReconciliationResult<SqlInstance>.Success(entity);
        }

        // Resolve SqlConfiguration if referenced
        SqlConfiguration? sqlConfig = null;
        if (entity.Spec.ConfigurationRef != null)
        {
            sqlConfig = await kubernetesClient.GetAsync<SqlConfiguration>(entity.Spec.ConfigurationRef.Name, ns, ct);
        }

        // Build and apply ConfigMap for mssql.conf
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, entity.Spec.Memory, entity.Spec.Storage);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);
        await ApplyConfigMapAsync(name, ns, confContent, entity, ct);

        // Build and apply StatefulSet (uses configHash for rolling restart detection)
        var sts = statefulSetBuilder.Build(entity, configHash);
        await ApplyStatefulSetAsync(sts, ct);

        // Build and apply Service
        var labels = new Dictionary<string, string> { { "mssql.operator/instance", name } };
        var owner = new V1OwnerReference
        {
            ApiVersion = entity.ApiVersion, Kind = entity.Kind,
            Name = name, Uid = entity.Metadata.Uid,
            Controller = true, BlockOwnerDeletion = true
        };

        var serviceSpec = entity.Spec.Service ?? new ServiceSpec();
        var svc = serviceBuilder.Build(name, ns, labels, serviceSpec, owner);
        await ApplyServiceAsync(svc, ct);

        // Update status
        var endpoint = $"{name}.{ns}.svc.cluster.local:{serviceSpec.Port}";
        entity = await UpdateStatus(entity, "Running", "InstanceRunning", "SQL Server instance is running", ct, endpoint);

        // Apply memory alignment if spec.memory is defined
        if (entity.Spec.Memory != null)
        {
            entity = await ApplyMemoryAlignmentAsync(entity, saPassword, ns, name, serviceSpec.Port, ct);
        }

        // Apply SqlConfiguration (sp_configure, traceflags, tempdb, databasemail) if referenced
        // The SqlConfiguration's own reconciliation loop handles applying to target instances.
        // Here we just ensure the config was resolved for the ConfigMap generation.
        if (sqlConfig != null)
        {
            logger.LogInformation("SqlConfiguration {Config} resolved for instance {Name}", sqlConfig.Metadata.Name, name);
        }

        // CiliumNetworkPolicy
        if (entity.Spec.NetworkPolicy?.Enabled == true)
        {
            var netpol = networkPolicyBuilder.BuildForInstance(name, ns, labels, serviceSpec.Port, owner,
                entity.Spec.NetworkPolicy.AllowedNamespaces, entity.Spec.NetworkPolicy.OperatorNamespace);
            try { await kubernetesClient.CreateAsync(netpol, ct); }
            catch { /* update if exists */ }
        }

        await eventPublisher.PublishAsync(entity, "InstanceProvisioned", $"SQL Server instance {name} is running", ct: ct);
        logger.LogInformation("SqlInstance {Name} reconciled successfully", name);
        return ReconciliationResult<SqlInstance>.Success(entity);
    }

    public Task<ReconciliationResult<SqlInstance>> DeletedAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("SqlInstance {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }

    private async Task ApplyConfigMapAsync(string name, string ns, string confContent, SqlInstance owner, CancellationToken ct)
    {
        var cmName = $"{name}-mssql-conf";
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = cmName,
                NamespaceProperty = ns,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = owner.ApiVersion, Kind = owner.Kind,
                        Name = owner.Metadata.Name, Uid = owner.Metadata.Uid,
                        Controller = true, BlockOwnerDeletion = true
                    }
                ]
            },
            Data = new Dictionary<string, string> { ["mssql.conf"] = confContent }
        };

        var existing = await kubernetesClient.GetAsync<V1ConfigMap>(cmName, ns, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(cm, ct);
        else
        {
            cm.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await kubernetesClient.UpdateAsync(cm, ct);
        }
    }

    private async Task ApplyStatefulSetAsync(V1StatefulSet sts, CancellationToken ct)
    {
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, sts.Metadata.NamespaceProperty, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(sts, ct);
        else
        {
            sts.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await kubernetesClient.UpdateAsync(sts, ct);
        }
    }

    private async Task ApplyServiceAsync(V1Service svc, CancellationToken ct)
    {
        var existing = await kubernetesClient.GetAsync<V1Service>(svc.Metadata.Name, svc.Metadata.NamespaceProperty, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(svc, ct);
        else
        {
            svc.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            svc.Spec.ClusterIP = existing.Spec.ClusterIP;
            await kubernetesClient.UpdateAsync(svc, ct);
        }
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SaPasswordSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            var key = entity.Spec.SaPasswordSecretRef.Key;
            return secret.Data.TryGetValue(key, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SqlInstance> UpdateStatus(SqlInstance entity, string phase, string reason, string message, CancellationToken ct, string? endpoint = null)
    {
        entity.Status ??= new SqlInstanceStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Version = entity.Spec.Version;
        entity.Status.Edition = entity.Spec.Edition;
        entity.Status.Endpoint = endpoint;
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready",
                Status = phase == "Running" ? "True" : "False",
                Reason = reason,
                Message = message,
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private async Task<SqlInstance> ApplyMemoryAlignmentAsync(SqlInstance entity, string saPassword, string ns, string name, int port, CancellationToken ct)
    {
        var memSpec = entity.Spec.Memory!;
        var totalMb = MemoryCalculator.ParseToMb(memSpec.Total);
        var reserveMb = MemoryCalculator.ParseToMb(memSpec.SystemReserve);
        var sqlMaxMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memSpec);
        var sqlMinMb = MemoryCalculator.CalculateSqlMinMemoryMb(memSpec);

        try
        {
            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);

            var currentConfig = await sqlServerManager.GetCurrentConfigAsync(conn, ct);
            var currentMax = currentConfig.GetValueOrDefault("max server memory (MB)", 0);
            var currentMin = currentConfig.GetValueOrDefault("min server memory (MB)", 0);
            var aligned = currentMax == sqlMaxMb && currentMin == sqlMinMb;

            if (!aligned)
            {
                if (currentMax != sqlMaxMb)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "max server memory (MB)", sqlMaxMb, ct);
                    logger.LogInformation("Applied max server memory = {MaxMb} MB on {Name} (pod limit: {TotalMb} MB, reserve: {ReserveMb} MB)",
                        sqlMaxMb, name, totalMb, reserveMb);
                }

                if (currentMin != sqlMinMb && sqlMinMb > 0)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "min server memory (MB)", sqlMinMb, ct);
                    logger.LogInformation("Applied min server memory = {MinMb} MB on {Name}", sqlMinMb, name);
                }

                await sqlServerManager.ReconfigureAsync(conn, ct: ct);

                await eventPublisher.PublishAsync(entity, "MemoryConfigured",
                    $"max server memory set to {sqlMaxMb} MB (pod limit: {totalMb} MB, system reserve: {reserveMb} MB)", ct: ct);
                aligned = true;
            }

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = aligned
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply memory alignment on {Name} — will retry", name);

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = false
            };

            await eventPublisher.PublishAsync(entity, "MemoryDriftDetected",
                $"max server memory should be {sqlMaxMb} MB but alignment failed: {ex.Message}", "Warning", ct);
        }

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return entity;
    }
}
