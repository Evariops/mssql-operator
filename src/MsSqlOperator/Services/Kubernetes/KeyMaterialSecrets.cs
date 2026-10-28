using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;

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

    /// <summary>
    /// Migrates a key-material Secret created by an earlier operator version to the retained shape.
    /// Only the machine key went through this migration; the SMK backup and its
    /// password Secrets kept the ownerReference they were created with, so on upgraded installations
    /// deleting the CR still garbage-collected them — exactly what an earlier fix claimed to prevent.
    /// Throws <see cref="ForeignChildException"/> when the Secret belongs to another kind.
    /// </summary>
    public static async Task<V1Secret> AdoptAsync(
        IKubernetesClient client, V1Secret secret, IKubernetesObject<V1ObjectMeta> owner,
        ILogger logger, CancellationToken ct)
    {
        ChildOwnershipGuard.EnsureOwnedBy(secret, owner,
            $"Secret {secret.Metadata.NamespaceProperty}/{secret.Metadata.Name}");

        var hasOwnerRefs = secret.Metadata.OwnerReferences is { Count: > 0 };
        var labels = secret.Metadata.Labels ??= new Dictionary<string, string>();
        if (!hasOwnerRefs && labels.ContainsKey(OperatorLabels.RetainedKeyMaterial))
            return secret; // already retained

        secret.Metadata.OwnerReferences = null;
        labels[OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue;
        labels[OperatorLabels.OwnerKind] = owner.Kind;
        labels[OperatorLabels.OwnerUid] = owner.Metadata.Uid ?? string.Empty;
        labels[OperatorLabels.RetainedKeyMaterial] = "true";
        (secret.Metadata.Annotations ??= new Dictionary<string, string>())[RetentionAnnotation] = Notice;

        try
        {
            var updated = await client.UpdateAsync(secret, ct);
            if (hasOwnerRefs)
                logger.LogWarning(
                    "Removed the ownerReference from key-material secret {Secret}: it must survive the deletion of its " +
                    "custom resource, because the PersistentVolumes it protects do", secret.Metadata.Name);
            return updated;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not migrate key-material secret {Secret} to the retained shape; will retry next pass",
                secret.Metadata.Name);
            return secret;
        }
    }

    /// <summary>
    /// The password protecting the SMK backups of <paramref name="owner"/>: the referenced Secret's,
    /// else the one the operator generated (<c>{name}-smk-backup-password</c>), created on first use.
    /// The generated Secret carries no ownerReference — losing it with the CR would make
    /// the SMK backup (which survives on its own) undecryptable. Shared by the instance and
    /// the availability group.
    /// </summary>
    public static Task<string?> GetOrCreateSmkBackupPasswordAsync(IKubernetesClient client,
        IKubernetesObject<V1ObjectMeta> owner, Entities.Common.SecretReference? reference, string ns, ILogger logger, CancellationToken ct)
        => GetOrCreatePasswordAsync(client, owner, reference, ns, "smk-backup-password", logger, ct);

    /// <summary>The password of the Database Master Key of an instance — never the SA one,
    /// whose rotation used to break TDE. Generated and retained like the SMK backup password.</summary>
    public static async Task<string> GetOrCreateMasterKeyPasswordAsync(IKubernetesClient client,
        IKubernetesObject<V1ObjectMeta> owner, string ns, ILogger logger, CancellationToken ct)
        => (await GetOrCreatePasswordAsync(client, owner, null, ns, "master-key-password", logger, ct))!;

    /// <summary>The referenced Secret's password, else the one generated in
    /// <c>{name}-{suffix}</c> (retained: no ownerReference), created on first use.</summary>
    private static async Task<string?> GetOrCreatePasswordAsync(IKubernetesClient client,
        IKubernetesObject<V1ObjectMeta> owner, Entities.Common.SecretReference? reference, string ns, string suffix, ILogger logger,
        CancellationToken ct)
    {
        if (reference != null && await SecretValues.TryGetAsync(client, reference, ns, ct) is { } referenced)
            return referenced;

        var passwordSecretName = $"{owner.Metadata.Name}-{suffix}";
        var existing = await client.GetAsync<V1Secret>(passwordSecretName, ns, ct);
        if (existing?.Data != null && existing.Data.TryGetValue("password", out var existingBytes))
        {
            // Migrate a legacy password Secret (it still carried an ownerReference).
            await AdoptAsync(client, existing, owner, logger, ct);
            return System.Text.Encoding.UTF8.GetString(existingBytes);
        }

        // The suffix guarantees the four character classes the SQL Server password policy may ask for.
        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "Aa1!";
        await client.CreateAsync(Build(passwordSecretName, ns, owner, new Dictionary<string, byte[]>
        {
            ["password"] = System.Text.Encoding.UTF8.GetBytes(password)
        }), ct);
        logger.LogInformation("Generated password secret {Secret}", passwordSecretName);
        return password;
    }

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
