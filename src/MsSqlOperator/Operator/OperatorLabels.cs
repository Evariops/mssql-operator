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
    /// Operational escape hatch: when set to <c>"true"</c> on any managed CR, the
    /// runtime skips the normal reconcile (spec→reality convergence) so an operator can perform
    /// manual surgery — split-brain recovery, SMK restore, replica re-provision — without the
    /// operator continuously re-applying labels/routing/REQUIRED_SYNC underneath. Deletion is NOT
    /// paused: finalizers still run so a paused resource can always be deleted.
    /// </summary>
    public const string PausedAnnotation = Group + "/reconcile-paused";
}
