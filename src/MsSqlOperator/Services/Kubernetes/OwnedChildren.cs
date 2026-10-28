using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Children a SqlInstance and a SqlAvailabilityGroup both own (each controller carried its
/// own copy of this code): the mssql.conf ConfigMap, the PodDisruptionBudget, and the guard on the
/// PVCs a previous resource left behind.
/// </summary>
public static class OwnedChildren
{
    /// <summary>Applies the <c>{name}-mssql-conf</c> ConfigMap. Server-side apply with
    /// force=true and a single shared field manager never conflicts, so a homonymous CR of the other
    /// kind silently rewrote it (content AND ownerReference) on every reconcile — ownership is checked
    /// before touching it.</summary>
    public static async Task ApplyMssqlConfAsync(IKubernetesClient client, IKubernetesObject<V1ObjectMeta> owner,
        string identityLabel, string confContent, CancellationToken ct)
    {
        var name = owner.Metadata.Name;
        var ns = owner.Metadata.NamespaceProperty;
        var cmName = $"{name}-mssql-conf";
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = cmName,
                NamespaceProperty = ns,
                // Scoping labels so the owned-resource watch selector covers the ConfigMap.
                Labels = new Dictionary<string, string>
                {
                    [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue,
                    [identityLabel] = name,
                },
                OwnerReferences = [ControllerReference(owner)],
            },
            Data = new Dictionary<string, string> { ["mssql.conf"] = confContent },
        };
        ChildOwnershipGuard.StampOwner(cm, owner);

        var existing = await client.GetAsync<V1ConfigMap>(cmName, ns, ct);
        ChildOwnershipGuard.EnsureOwnedBy(existing, owner, $"ConfigMap {ns}/{cmName}");

        // Server-side apply.
        await client.ApplyAsync(cm, ct);
    }

    /// <summary>Creates or updates the <c>{name}-pdb</c> PodDisruptionBudget over the pods carrying
    /// <paramref name="selector"/>. Best effort: a failure is logged, the reconcile goes on.</summary>
    public static async Task ApplyPodDisruptionBudgetAsync(IKubernetesClient client, ILogger logger,
        IKubernetesObject<V1ObjectMeta> owner, Dictionary<string, string> selector, int maxUnavailable, CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty;
        var pdb = new V1PodDisruptionBudget
        {
            ApiVersion = "policy/v1",
            Kind = "PodDisruptionBudget",
            Metadata = new V1ObjectMeta
            {
                Name = $"{owner.Metadata.Name}-pdb",
                NamespaceProperty = ns,
                // managed-by on metadata only; the PDB selector is immutable.
                Labels = new Dictionary<string, string>(selector) { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue },
                OwnerReferences = [ControllerReference(owner)],
            },
            Spec = new V1PodDisruptionBudgetSpec
            {
                MaxUnavailable = maxUnavailable,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(selector) },
            },
        };

        try
        {
            // Server-side apply — the PUT wrote back the fields others had defaulted.
            await client.ApplyAsync(pdb, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to apply PodDisruptionBudget for {Kind} {Name}", owner.Kind, owner.Metadata.Name);
        }
    }

    /// <summary>
    /// The PVCs of a StatefulSet are named <c>{template}-{sts}-{ordinal}</c> and outlive the CR (no
    /// ownerReference, no retention policy). A CR of a DIFFERENT kind created on the same name would
    /// therefore start SQL Server on the previous tenant's databases: refused. A same-kind
    /// recreate legitimately adopts its own data, but says so out loud. The PVCs are stamped with their
    /// owner at reconcile time (nothing ever wrote the labels the guard reads — templates
    /// are immutable — so the refusal could never fire).
    /// </summary>
    public static async Task GuardExistingDataVolumesAsync(IKubernetesClient client, IEventPublisher eventPublisher, ILogger logger,
        IKubernetesObject<V1ObjectMeta> owner, V1StatefulSet sts, int replicas, CancellationToken ct)
    {
        var ns = sts.Metadata.NamespaceProperty;
        foreach (var template in sts.Spec?.VolumeClaimTemplates ?? [])
        {
            if (template.Metadata?.Name is not { } tplName) continue;
            for (var ordinal = 0; ordinal < replicas; ordinal++)
            {
                var pvcName = $"{tplName}-{sts.Metadata.Name}-{ordinal}";
                var pvc = await client.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
                if (pvc is null) continue;

                ChildOwnershipGuard.EnsureOwnedBy(pvc, owner, $"PersistentVolumeClaim {ns}/{pvcName}");

                if (ChildOwnershipGuard.IsStaleSameKindChild(pvc, owner))
                    await eventPublisher.PublishAsync(owner, "AdoptedExistingData",
                        $"PersistentVolumeClaim {pvcName} was left behind by a previous {owner.Kind} of the same name; " +
                        "this resource starts on its existing data files. Delete the PVCs first if you wanted a blank one.",
                        "Warning", ct);

                if (!ChildOwnershipGuard.IsStampedBy(pvc, owner))
                {
                    ChildOwnershipGuard.StampOwner(pvc, owner);
                    try { await client.PatchAsync(pvc, [JsonPatch.Replace("/metadata/labels", pvc.Metadata.Labels)], ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not stamp ownership labels on PVC {Pvc}", pvcName);
                    }
                }
            }
        }
    }

    private static V1OwnerReference ControllerReference(IKubernetesObject<V1ObjectMeta> owner) => new()
    {
        ApiVersion = owner.ApiVersion,
        Kind = owner.Kind,
        Name = owner.Metadata.Name,
        Uid = owner.Metadata.Uid,
        Controller = true,
        BlockOwnerDeletion = true,
    };
}
