using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Replaces a StatefulSet whose immutable fields must change (volumeClaimTemplates, selector) around
/// its live pods: an orphan delete keeps the pods and their PVCs, and the StatefulSet the caller
/// applies next adopts the pods its selector matches. A pod template identical to the adopted
/// revision restarts nothing.
/// </summary>
public static class StatefulSetRecreation
{
    /// <summary>True when the live selector differs from the desired one (StatefulSets built
    /// before the selector was reduced to the identity label still carry version and edition). A live
    /// object without a selector is left alone: the API server never returns one.</summary>
    public static bool SelectorChanged(V1StatefulSet live, V1StatefulSet desired)
    {
        var current = live.Spec?.Selector;
        var wanted = desired.Spec?.Selector;
        if (current?.MatchLabels is null || wanted?.MatchLabels is null)
            return false;

        return current.MatchExpressions is { Count: > 0 }
               || current.MatchLabels.Count != wanted.MatchLabels.Count
               || current.MatchLabels.Any(kv => !wanted.MatchLabels.TryGetValue(kv.Key, out var v) || v != kv.Value);
    }

    /// <summary>
    /// Grows the PVCs whose template size increased (shrinking is refused at admission).
    /// Returns true when the StatefulSet must then be recreated to carry the new templates.
    /// </summary>
    public static async Task<bool> ExpandVolumeClaimsAsync(IKubernetesClient client, ILogger logger,
        V1StatefulSet existing, V1StatefulSet desired, CancellationToken ct)
    {
        var ns = desired.Metadata.NamespaceProperty;
        var grown = false;
        var existingClaims = (existing.Spec?.VolumeClaimTemplates ?? [])
            .Where(c => c.Metadata?.Name != null)
            .ToDictionary(c => c.Metadata.Name);

        foreach (var template in desired.Spec?.VolumeClaimTemplates ?? [])
        {
            if (template.Metadata?.Name is null || !existingClaims.TryGetValue(template.Metadata.Name, out var current))
                continue;
            ResourceQuantity? desiredSize = null;
            ResourceQuantity? currentSize = null;
            template.Spec?.Resources?.Requests?.TryGetValue("storage", out desiredSize);
            current.Spec?.Resources?.Requests?.TryGetValue("storage", out currentSize);
            if (desiredSize is null || currentSize is null || desiredSize.ToString() == currentSize.ToString())
                continue;

            grown = true;
            var replicas = existing.Spec?.Replicas ?? 1;
            for (var ordinal = 0; ordinal < replicas; ordinal++)
            {
                var pvcName = $"{template.Metadata.Name}-{existing.Metadata.Name}-{ordinal}";
                var pvc = await client.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
                if (pvc?.Spec?.Resources?.Requests == null) continue;
                // An AG volume may have grown past its template on its own; a PVC never shrinks.
                if (pvc.Spec.Resources.Requests.TryGetValue("storage", out var pvcSize) && pvcSize.ToDecimal() >= desiredSize.ToDecimal())
                    continue;
                pvc.Spec.Resources.Requests["storage"] = desiredSize;
                await client.UpdateAsync(pvc, ct);
                logger.LogInformation("Expanded PVC {Pvc} to {Size}", pvcName, desiredSize.ToString());
            }
        }

        return grown;
    }

    /// <summary>
    /// Orphan-deletes the StatefulSet and waits for the API server to release its name. When it is
    /// still there after the wait, throws <see cref="StatefulSetNameNotReleasedException"/>: applying
    /// on top of the dying object fails with a 422, the caller requeues instead.
    /// </summary>
    public static async Task OrphanDeleteAsync(IKubernetesClient client, string name, string ns, string reason, CancellationToken ct)
    {
        await client.DeleteStatefulSetOrphanAsync(name, ns, ct);
        for (var i = 0; i < 20 && await client.GetAsync<V1StatefulSet>(name, ns, ct) != null; i++)
            await Task.Delay(500, ct);
        if (await client.GetAsync<V1StatefulSet>(name, ns, ct) != null)
            throw new StatefulSetNameNotReleasedException(
                $"StatefulSet {ns}/{name} is still terminating after {reason}; retrying shortly.");
    }
}
