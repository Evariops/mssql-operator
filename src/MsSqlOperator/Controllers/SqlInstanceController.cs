using k8s;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlInstance), Verbs = RbacVerb.All)]
public class SqlInstanceController(
    ILogger<SqlInstanceController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder) : IEntityController<SqlInstance>
{
    public async Task<ReconciliationResult<SqlInstance>> ReconcileAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlInstance {Name} in {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // Resolve SA password
        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for {Name}", name);
            entity = await UpdateStatus(entity, "Failed", "SaPasswordNotFound", "SA password secret not found", ct);
            return ReconciliationResult<SqlInstance>.Success(entity);
        }

        // Build and apply StatefulSet
        var sts = statefulSetBuilder.Build(entity, saPassword);
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

        // CiliumNetworkPolicy
        if (entity.Spec.EnableNetworkPolicy)
        {
            var netpol = networkPolicyBuilder.BuildForInstance(name, ns, labels, serviceSpec.Port, owner);
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
}
