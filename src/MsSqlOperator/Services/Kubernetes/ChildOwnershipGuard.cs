using k8s;
using k8s.Models;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Raised when a child resource the controller is about to mutate belongs to a DIFFERENT custom
/// resource. The controller surfaces it as a Ready=False condition + event
/// instead of proceeding.
/// </summary>
public class ForeignChildException(string message) : InvalidOperationException(message);

/// <summary>
/// Every child the operator creates is named after its CR — StatefulSet <c>x</c>, ConfigMap
/// <c>x-mssql-conf</c>, PDB <c>x-pdb</c>, Secrets <c>x-machine-key</c>, PVCs <c>data-x-0</c>. But
/// Kubernetes only enforces name uniqueness <em>per kind</em>: a <c>SqlInstance</c> and a
/// <c>SqlAvailabilityGroup</c> can both be called <c>x</c> in the same namespace, and then they
/// share every one of those children.
///
/// The consequences ranged from a silently corrupted ConfigMap (server-side
/// apply with the same field manager and force=true never conflicts) to the destruction of a
/// production AG: the SqlInstance controller found "its" StatefulSet <c>x</c>, saw a different
/// storage size, expanded the AG's PVCs and then deleted the AG's StatefulSet
/// (<c>DeleteStatefulSetOrphanAsync</c>), orphaning its pods for good.
///
/// This guard is the single check every mutating path runs first: <b>only touch a child that
/// belongs to me.</b> Ownership is read from the ownerReference (authoritative) and, for children
/// that carry no ownerReference by design (key-material Secrets, PVCs created by the StatefulSet
/// controller), from the owner-kind/owner-uid labels.
/// </summary>
public static class ChildOwnershipGuard
{
    /// <summary>Labels stamped on every operator-generated child so its owner is identifiable even
    /// without an ownerReference.</summary>
    public static void StampOwner<T>(T child, IKubernetesObject<V1ObjectMeta> owner)
        where T : IKubernetesObject<V1ObjectMeta>
    {
        child.Metadata.Labels ??= new Dictionary<string, string>();
        child.Metadata.Labels[OperatorLabels.OwnerKind] = owner.Kind;
        // A UID is not a valid label value in full (it contains dashes, which are fine) — UIDs are
        // RFC-4122, so they satisfy the label value charset (alphanumerics, '-', 63 chars max).
        child.Metadata.Labels[OperatorLabels.OwnerUid] = owner.Metadata.Uid ?? string.Empty;
    }

    /// <summary>
    /// Throws when <paramref name="existing"/> belongs to a CR of a DIFFERENT kind.
    ///
    /// The comparison is on the kind, not the UID, and that is deliberate: within one kind, the same
    /// name in the same namespace IS the same object, so a UID mismatch can only mean a stale child
    /// of a deleted CR of the same kind — which is exactly what the retention of PVCs and key
    /// material is meant to let the recreated CR adopt. What must never happen is a
    /// SqlInstance adopting a SqlAvailabilityGroup's children, or the reverse.
    ///
    /// A child with no ownership information at all is adoptable (it predates this operator version).
    /// </summary>
    public static void EnsureOwnedBy(IKubernetesObject<V1ObjectMeta>? existing, IKubernetesObject<V1ObjectMeta> owner, string childDescription)
    {
        if (existing is null)
            return;

        var controllerRef = existing.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
        if (controllerRef is not null && !string.IsNullOrEmpty(controllerRef.Kind))
        {
            if (!string.Equals(controllerRef.Kind, owner.Kind, StringComparison.Ordinal))
                throw new ForeignChildException(
                    $"{childDescription} already belongs to {controllerRef.Kind}/{controllerRef.Name}; " +
                    $"refusing to modify a resource owned by another kind of custom resource. " +
                    $"Rename this {owner.Kind} or delete the conflicting {controllerRef.Kind}.");
            return;
        }

        if (existing.Metadata.Labels is not { } labels
            || !labels.TryGetValue(OperatorLabels.OwnerKind, out var kind)
            || string.IsNullOrEmpty(kind))
            return; // unstamped legacy child — adoptable

        if (!string.Equals(kind, owner.Kind, StringComparison.Ordinal))
            throw new ForeignChildException(
                $"{childDescription} is labelled as belonging to a {kind}, not to this {owner.Kind}; " +
                "refusing to modify it. Rename this resource or remove the conflicting one.");
    }

    /// <summary>True when the child was last owned by a CR of the same kind but a different UID —
    /// i.e. a deliberately retained child (PVC, key Secret) left behind by a previous CR of the same
    /// name. Adoptable, but worth telling the user about.</summary>
    public static bool IsStaleSameKindChild(IKubernetesObject<V1ObjectMeta> existing, IKubernetesObject<V1ObjectMeta> owner)
        => existing.Metadata.Labels is { } labels
           && labels.TryGetValue(OperatorLabels.OwnerKind, out var kind) && kind == owner.Kind
           && labels.TryGetValue(OperatorLabels.OwnerUid, out var uid)
           && !string.IsNullOrEmpty(uid)
           && uid != owner.Metadata.Uid;
}
