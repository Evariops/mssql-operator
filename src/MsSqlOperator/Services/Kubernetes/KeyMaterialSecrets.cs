using k8s;
using k8s.Models;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Builds the Secrets that hold key material (machine key, Service Master Key backup and the
/// password protecting it).
///
/// These Secrets deliberately carry NO ownerReference. They used to, and the
/// consequence was severe: deleting a CR — including the delete/recreate the product itself
/// prescribes to change <c>spec.edition</c> — garbage-collected the keys while the PVCs survived
/// (no <c>persistentVolumeClaimRetentionPolicy</c>, reclaimPolicy Retain). SQL Server then came back
/// on the old data files with a freshly generated machine key, could no longer decrypt its Service
/// Master Key, and every TDE database and stored credential became unreadable — with the SMK backup
/// that would have saved them deleted by the same garbage collection.
///
/// Key material outlives the resource that created it. Removing it is a deliberate human act.
/// </summary>
public static class KeyMaterialSecrets
{
    public const string RetentionAnnotation = "mssql-operator.evariops.com/retention-notice";

    private const string Notice =
        "Key material: this Secret carries no ownerReference on purpose, so deleting the custom resource " +
        "does not destroy it while the PersistentVolumes it protects survive. Delete it explicitly, after the PVCs.";

    public static V1Secret Build(string name, string ns, IKubernetesObject<V1ObjectMeta> owner, Dictionary<string, byte[]> data)
        => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    // Managed-by for inventory (Secrets are excluded from the owned-resource watch).
                    [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue,
                    [OperatorLabels.OwnerKind] = owner.Kind,
                    [OperatorLabels.OwnerUid] = owner.Metadata.Uid ?? string.Empty,
                    [OperatorLabels.RetainedKeyMaterial] = "true",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false",
                    [RetentionAnnotation] = Notice,
                },
                // No OwnerReferences — see the type comment.
            },
            Type = "Opaque",
            Data = data,
        };
}
