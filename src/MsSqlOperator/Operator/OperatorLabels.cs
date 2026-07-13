namespace MsSqlOperator.Operator;

/// <summary>
/// Well-known label keys/values the operator stamps on the resources it generates. Centralised so
/// the owned-resource watch selector and the builders agree on a single source of truth.
/// </summary>
public static class OperatorLabels
{
    public const string Group = "mssql-operator.evariops.com";

    /// <summary>
    /// Common scoping label on <b>every</b> operator-generated child (StatefulSet, Service,
    /// ConfigMap, CiliumNetworkPolicy, PodDisruptionBudget, owned Secrets). It bounds the
    /// owned-resource watch to operator-managed objects and doubles as a human inventory handle
    /// (<c>kubectl get all,cm,secret -l mssql-operator.evariops.com/managed-by=mssql-operator</c>).
    /// </summary>
    public const string ManagedBy = "mssql-operator.evariops.com/managed-by";

    public const string ManagedByValue = "mssql-operator";

    /// <summary>Scoping label tying a child to its owning <c>SqlInstance</c>.</summary>
    public const string Instance = "mssql-operator.evariops.com/instance";

    /// <summary>Scoping label tying a child to its owning <c>SqlAvailabilityGroup</c>.</summary>
    public const string Ag = "mssql-operator.evariops.com/ag";

    /// <summary>
    /// Optional cross-resource grouping label. Set from <c>spec.pool</c>, stamped on the
    /// pod template (and StatefulSet metadata) but never the immutable selector, so pool membership
    /// stays mutable. Drives cross-instance topology spread and pool anti-affinity.
    /// </summary>
    public const string Pool = "mssql-operator.evariops.com/pool";

    /// <summary>The selector the owned-resource watch uses to list/watch only operator-managed children.</summary>
    public const string ManagedBySelector = ManagedBy + "=" + ManagedByValue;

    /// <summary>
    /// Kind of the CR a child belongs to. Children are named after their CR
    /// (<c>x</c>, <c>x-mssql-conf</c>, <c>data-x-0</c>, <c>x-machine-key</c>…) and a SqlInstance and a
    /// SqlAvailabilityGroup may legally share the name <c>x</c> — Kubernetes only enforces uniqueness
    /// per kind. Without this label the second CR silently adopts (and corrupts) the first one's
    /// children. Paired with <see cref="OwnerUid"/> it also detects a stale child left behind by a
    /// deleted CR of the same name.
    /// </summary>
    public const string OwnerKind = Group + "/owner-kind";

    /// <summary>UID of the CR a child belongs to — see <see cref="OwnerKind"/>.</summary>
    public const string OwnerUid = Group + "/owner-uid";

    /// <summary>
    /// Set on key-material Secrets (machine key, SMK backup and its password) that deliberately
    /// carry NO ownerReference: deleting the CR must not garbage-collect the keys while
    /// the PVCs — and therefore the encrypted data — survive. Deleting these Secrets is a manual,
    /// explicit act.
    /// </summary>
    public const string RetainedKeyMaterial = Group + "/retained-key-material";

    /// <summary>
    /// Operational escape hatch: when set to <c>"true"</c> on any managed CR, the
    /// runtime skips the normal reconcile (spec→reality convergence) so an operator can perform
    /// manual surgery — split-brain recovery, SMK restore, replica re-provision — without the
    /// operator continuously re-applying labels/routing/REQUIRED_SYNC underneath. Deletion is NOT
    /// paused: finalizers still run so a paused resource can always be deleted.
    /// </summary>
    public const string PausedAnnotation = Group + "/reconcile-paused";
}
