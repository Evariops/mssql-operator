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

[EntityRbac(typeof(SqlAvailabilityGroup), Verbs = RbacVerb.All)]
public class SqlAvailabilityGroupController(
    ILogger<SqlAvailabilityGroupController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder) : IEntityController<SqlAvailabilityGroup>
{
    public async Task<ReconciliationResult<SqlAvailabilityGroup>> ReconcileAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlAvailabilityGroup {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for AG {Name}", name);
            return ReconciliationResult<SqlAvailabilityGroup>.Success(entity);
        }

        // Build StatefulSet for AG
        var sts = statefulSetBuilder.BuildForAg(entity, saPassword);
        await ApplyStatefulSetAsync(sts, ct);

        var labels = new Dictionary<string, string> { { "mssql.operator/ag", name } };
        var owner = new V1OwnerReference
        {
            ApiVersion = entity.ApiVersion, Kind = entity.Kind,
            Name = name, Uid = entity.Metadata.Uid,
            Controller = true, BlockOwnerDeletion = true
        };

        // Headless service for pod DNS
        var headless = serviceBuilder.BuildHeadless($"{name}-headless", ns, labels, 1433, owner);
        await ApplyServiceAsync(headless, ct);

        // Listener service
        if (entity.Spec.Listener != null)
        {
            var listenerSvc = serviceBuilder.Build(
                $"{name}-listener", ns, labels,
                entity.Spec.Listener.Service ?? new ServiceSpec { Port = entity.Spec.Listener.Port },
                owner);
            await ApplyServiceAsync(listenerSvc, ct);
        }

        // Read-only listener
        if (entity.Spec.ReadOnlyListener != null)
        {
            var roSvc = serviceBuilder.Build(
                $"{name}-ro", ns, labels,
                entity.Spec.ReadOnlyListener.Service ?? new ServiceSpec { Port = entity.Spec.ReadOnlyListener.Port },
                owner);
            await ApplyServiceAsync(roSvc, ct);
        }

        // Update status
        entity.Status ??= new SqlAvailabilityGroupStatus();
        entity.Status.Phase = "Running";
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.ListenerEndpoint = $"{name}-listener.{ns}.svc.cluster.local:1433";
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready", Status = "True", Reason = "AgReady",
                Message = "Availability Group resources created", LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        // CiliumNetworkPolicy
        if (entity.Spec.EnableNetworkPolicy)
        {
            var netpol = networkPolicyBuilder.BuildForAg(name, ns, labels, 1433, 5022, owner);
            try { await kubernetesClient.CreateAsync(netpol, ct); }
            catch { /* update if exists */ }
        }

        await eventPublisher.PublishAsync(entity, "AgReady", $"Availability Group {name} resources created", ct: ct);
        logger.LogInformation("SqlAvailabilityGroup {Name} reconciled", name);
        return ReconciliationResult<SqlAvailabilityGroup>.Success(entity);
    }

    public Task<ReconciliationResult<SqlAvailabilityGroup>> DeletedAsync(SqlAvailabilityGroup entity, CancellationToken ct)
    {
        logger.LogInformation("SqlAvailabilityGroup {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlAvailabilityGroup>.Success(entity));
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

    private async Task<string?> ResolveSaPasswordAsync(SqlAvailabilityGroup entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SaPasswordSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(entity.Spec.SaPasswordSecretRef.Key, out var bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }
}
